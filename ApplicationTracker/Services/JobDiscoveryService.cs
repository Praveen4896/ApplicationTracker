using System.Net;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace ApplicationTracker.Services;

public sealed class JobDiscoveryService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim>
        SourceLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient httpClient;
    private readonly IConfiguration configuration;
    private readonly IMemoryCache memoryCache;
    private readonly JobSearchEngine searchEngine;

    public JobDiscoveryService(
        HttpClient httpClient,
        IConfiguration configuration,
        IMemoryCache memoryCache,
        JobSearchEngine searchEngine)
    {
        this.httpClient = httpClient;
        this.configuration = configuration;
        this.memoryCache = memoryCache;
        this.searchEngine = searchEngine;
    }

    public Task<JobDiscoveryResponse> SearchAsync(
        string? keyword,
        string? location,
        CancellationToken cancellationToken = default)
    {
        return SearchAsync(
            new JobDiscoverySearchRequest
            {
                Keyword = keyword ?? string.Empty,
                Location = location ?? string.Empty
            },
            cancellationToken);
    }

    public async Task<JobDiscoveryResponse> SearchAsync(
        JobDiscoverySearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var sources = configuration
            .GetSection("JobDiscovery:Sources")
            .Get<List<JobDiscoverySourceOptions>>()
            ?? new List<JobDiscoverySourceOptions>();

        var enabledSources = sources
            .Where(source => source.Enabled)
            .Where(source => !string.IsNullOrWhiteSpace(source.Provider))
            .Where(source => !string.IsNullOrWhiteSpace(source.BoardIdentifier))
            .ToList();

        var response = new JobDiscoveryResponse
        {
            SourceCount = enabledSources.Count
        };

        if (enabledSources.Count == 0)
        {
            response.Warnings.Add(
                "No job sources are enabled in appsettings.json.");
            return response;
        }

        var sourceTasks = enabledSources.Select(source =>
            LoadSourceSafelyAsync(source, cancellationToken));

        var sourceResults = await Task.WhenAll(sourceTasks);

        foreach (var sourceResult in sourceResults)
        {
            if (!string.IsNullOrWhiteSpace(sourceResult.Warning))
            {
                response.Warnings.Add(sourceResult.Warning);
                continue;
            }

            response.SuccessfulSourceCount++;
            response.AllJobsLoaded += sourceResult.Jobs.Count;
            response.Jobs.AddRange(sourceResult.Jobs.Select(CloneJob));
        }

        return FilterCatalog(response.Jobs, request, response);
    }

    public JobDiscoveryResponse FilterCatalog(
        IEnumerable<DiscoveredJobResult> catalog,
        JobDiscoverySearchRequest request,
        JobDiscoveryResponse? sourceInformation = null)
    {
        var response = new JobDiscoveryResponse
        {
            SourceCount = sourceInformation?.SourceCount ?? 0,
            SuccessfulSourceCount = sourceInformation?.SuccessfulSourceCount ?? 0,
            AllJobsLoaded = sourceInformation?.AllJobsLoaded ?? 0
        };

        if (sourceInformation is not null)
        {
            response.Warnings.AddRange(sourceInformation.Warnings);
        }

        var filterContext = BuildFilterContext(request);

        var filteredJobs = catalog
            .Select(CloneJob)
            .Where(job => MatchesSearch(job, request, filterContext))
            .GroupBy(GetDeduplicationKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        filteredJobs = request.SortOrder.ToLowerInvariant() switch
        {
            "relevance" => filteredJobs
                .OrderByDescending(job => job.RelevanceScore)
                .ThenByDescending(job => job.PostedDate.HasValue)
                .ThenByDescending(job => job.PostedDate)
                .ThenBy(job => job.CompanyName)
                .ToList(),

            "company" => filteredJobs
                .OrderBy(job => job.CompanyName)
                .ThenBy(job => job.PositionTitle)
                .ToList(),

            "title" => filteredJobs
                .OrderBy(job => job.PositionTitle)
                .ThenBy(job => job.CompanyName)
                .ToList(),

            _ => filteredJobs
                .OrderByDescending(job => job.PostedDate.HasValue)
                .ThenByDescending(job => job.PostedDate)
                .ThenBy(job => job.CompanyName)
                .ToList()
        };

        response.TotalCount = filteredJobs.Count;
        response.PageSize = Math.Clamp(request.PageSize, 5, 5000);
        response.TotalPages = Math.Max(
            1,
            (int)Math.Ceiling(
                response.TotalCount / (double)response.PageSize));
        response.Page = Math.Clamp(request.Page, 1, response.TotalPages);

        var pagedJobs = filteredJobs
            .Skip((response.Page - 1) * response.PageSize)
            .Take(response.PageSize)
            .ToList();

        response.Jobs.Clear();
        response.Jobs.AddRange(pagedJobs);

        return response;
    }

    public async Task<DiscoveredJobResult> GetFullDetailsAsync(
        DiscoveredJobResult job,
        CancellationToken cancellationToken = default)
    {
        var completeJob = CloneJob(job);

        if (!job.Source.Equals(
                "SmartRecruiters",
                StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(job.SourceIdentifier)
            || string.IsNullOrWhiteSpace(job.ExternalJobId))
        {
            return completeJob;
        }

        try
        {
            var url =
                "https://api.smartrecruiters.com/v1/companies/"
                + Uri.EscapeDataString(job.SourceIdentifier)
                + "/postings/"
                + Uri.EscapeDataString(job.ExternalJobId);

            using var response = await httpClient.GetAsync(
                url,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return completeJob;
            }

            await using var stream =
                await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken);

            ApplySmartRecruitersDetails(
                completeJob,
                document.RootElement);
            NormalizeJob(completeJob);

            return completeJob;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // The summary still contains a working employer link and enough
            // information to create a tracked application.
            return completeJob;
        }
    }

    private async Task<SourceLoadResult> LoadSourceSafelyAsync(
        JobDiscoverySourceOptions source,
        CancellationToken cancellationToken)
    {
        try
        {
            using var sourceTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sourceTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var sourceToken = sourceTimeout.Token;

            var cacheKey =
                $"job-discovery:{source.Provider.Trim().ToLowerInvariant()}:{source.BoardIdentifier.Trim().ToLowerInvariant()}";

            if (memoryCache.TryGetValue(
                    cacheKey,
                    out List<DiscoveredJobResult>? cachedJobs)
                && cachedJobs is not null)
            {
                return new SourceLoadResult(cachedJobs, null);
            }

            var sourceLock = SourceLocks.GetOrAdd(
                cacheKey,
                _ => new SemaphoreSlim(1, 1));
            await sourceLock.WaitAsync(sourceToken);

            try
            {
                // A background warm-up or another browser request may have
                // populated the cache while this request waited for the lock.
                if (memoryCache.TryGetValue(
                        cacheKey,
                        out cachedJobs)
                    && cachedJobs is not null)
                {
                    return new SourceLoadResult(cachedJobs, null);
                }

                var jobs = source.Provider.Trim().ToLowerInvariant() switch
                {
                    "ashby" => await GetAshbyJobsAsync(source, sourceToken),
                    "greenhouse" => await GetGreenhouseJobsAsync(source, sourceToken),
                    "lever" => await GetLeverJobsAsync(source, sourceToken),
                    "smartrecruiters" => await GetSmartRecruitersJobsAsync(
                        source,
                        sourceToken),
                    "remotive" => await GetRemotiveJobsAsync(source, sourceToken),
                    "jobicy" => await GetJobicyJobsAsync(source, sourceToken),
                    _ => throw new InvalidOperationException(
                        $"Unsupported provider '{source.Provider}'.")
                };

                foreach (var job in jobs)
                {
                    NormalizeJob(job);
                }

                memoryCache.Set(
                    cacheKey,
                    jobs,
                    new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow =
                            GetSourceCacheDuration(source.Provider)
                    });

                return new SourceLoadResult(jobs, null);
            }
            finally
            {
                sourceLock.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new SourceLoadResult(
                new List<DiscoveredJobResult>(),
                $"{source.CompanyName} ({source.Provider}): source timed out.");
        }
        catch (Exception exception)
        {
            return new SourceLoadResult(
                new List<DiscoveredJobResult>(),
                $"{source.CompanyName} ({source.Provider}): {FriendlyError(exception)}");
        }
    }

    private async Task<List<DiscoveredJobResult>> GetRemotiveJobsAsync(
        JobDiscoverySourceOptions source,
        CancellationToken cancellationToken)
    {
        const string url = "https://remotive.com/api/remote-jobs";

        using var response = await httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);

        var jobs = new List<DiscoveredJobResult>();
        if (!document.RootElement.TryGetProperty("jobs", out var jobElements)
            || jobElements.ValueKind != JsonValueKind.Array)
        {
            return jobs;
        }

        foreach (var job in jobElements.EnumerateArray())
        {
            var jobUrl = GetString(job, "url");
            jobs.Add(new DiscoveredJobResult
            {
                ExternalJobId = GetValueAsString(job, "id"),
                Source = "Remotive",
                CompanyName = GetString(job, "company_name"),
                PositionTitle = GetString(job, "title"),
                Location = GetString(job, "candidate_required_location"),
                JobDescription = CleanHtml(GetString(job, "description")),
                JobPostingUrl = jobUrl,
                ApplyUrl = jobUrl,
                Department = GetString(job, "category"),
                EmploymentType = GetString(job, "job_type"),
                WorkplaceType = "Remote",
                SalaryText = GetString(job, "salary"),
                PostedDate = ParseDate(GetString(job, "publication_date")),
                IsVerifiedSource = false
            });
        }

        return jobs;
    }

    private async Task<List<DiscoveredJobResult>> GetJobicyJobsAsync(
        JobDiscoverySourceOptions source,
        CancellationToken cancellationToken)
    {
        const string url =
            "https://jobicy.com/api/v2/remote-jobs?count=100&geo=usa";

        using var response = await httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);

        var jobs = new List<DiscoveredJobResult>();
        if (!document.RootElement.TryGetProperty("jobs", out var jobElements)
            || jobElements.ValueKind != JsonValueKind.Array)
        {
            return jobs;
        }

        foreach (var job in jobElements.EnumerateArray())
        {
            var jobUrl = GetString(job, "url");

            jobs.Add(new DiscoveredJobResult
            {
                ExternalJobId = GetValueAsString(job, "id"),
                Source = "Jobicy",
                CompanyName = GetString(job, "companyName"),
                PositionTitle = GetString(job, "jobTitle"),
                Location = GetString(job, "jobGeo"),
                Department = GetArrayText(job, "jobIndustry"),
                JobDescription = FirstNonEmpty(
                    CleanHtml(GetString(job, "jobDescription")),
                    GetString(job, "jobExcerpt")),
                JobPostingUrl = jobUrl,
                ApplyUrl = jobUrl,
                EmploymentType = GetArrayText(job, "jobType"),
                WorkplaceType = "Remote",
                ExperienceLevel = GetString(job, "jobLevel"),
                SalaryText = ReadJobicySalary(job),
                PostedDate = ParseDate(GetString(job, "pubDate")),
                IsVerifiedSource = false
            });
        }

        return jobs;
    }

    private async Task<List<DiscoveredJobResult>> GetSmartRecruitersJobsAsync(
        JobDiscoverySourceOptions source,
        CancellationToken cancellationToken)
    {
        const int pageSize = 100;
        var maximumPages = Math.Clamp(source.MaximumPages, 1, 10);
        var jobs = new List<DiscoveredJobResult>();

        for (var page = 0; page < maximumPages; page++)
        {
            var offset = page * pageSize;
            var url =
                "https://api.smartrecruiters.com/v1/companies/"
                + Uri.EscapeDataString(source.BoardIdentifier)
                + "/postings?destination=PUBLIC&country=us"
                + $"&limit={pageSize}&offset={offset}";

            using var response = await httpClient.GetAsync(
                url,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream =
                await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken);

            if (!document.RootElement.TryGetProperty(
                    "content",
                    out var jobElements)
                || jobElements.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            foreach (var job in jobElements.EnumerateArray())
            {
                var externalJobId = FirstNonEmpty(
                    GetString(job, "id"),
                    GetString(job, "uuid"));
                var title = GetString(job, "name");
                var companyName = ReadNestedLabel(job, "company", "name");
                var locationText = ReadSmartRecruitersLocation(job);
                var department = FirstNonEmpty(
                    ReadNestedLabel(job, "department", "label"),
                    ReadNestedLabel(job, "function", "label"));
                var employmentType =
                    ReadNestedLabel(job, "typeOfEmployment", "label");
                var experienceLevel =
                    ReadNestedLabel(job, "experienceLevel", "label");
                var workplaceType =
                    ReadSmartRecruitersWorkplaceType(job);
                var postingUrl = BuildSmartRecruitersPostingUrl(
                    source.BoardIdentifier,
                    externalJobId,
                    title);

                jobs.Add(new DiscoveredJobResult
                {
                    ExternalJobId = externalJobId,
                    Source = "SmartRecruiters",
                    SourceIdentifier = source.BoardIdentifier,
                    CompanyName = FirstNonEmpty(
                        companyName,
                        source.CompanyName),
                    PositionTitle = title,
                    Location = locationText,
                    Department = department,
                    JobDescription = BuildSmartRecruitersSummary(
                        title,
                        FirstNonEmpty(companyName, source.CompanyName),
                        locationText,
                        department,
                        employmentType,
                        experienceLevel),
                    JobPostingUrl = postingUrl,
                    ApplyUrl = postingUrl,
                    EmploymentType = employmentType,
                    WorkplaceType = workplaceType,
                    ExperienceLevel = experienceLevel,
                    PostedDate = ParseDate(GetString(job, "releasedDate")),
                    IsVerifiedSource = true
                });
            }

            var totalFound = GetInteger(
                document.RootElement,
                "totalFound");
            var received = jobElements.GetArrayLength();

            if (received < pageSize
                || (totalFound > 0 && offset + received >= totalFound))
            {
                break;
            }
        }

        return jobs;
    }

    private async Task<List<DiscoveredJobResult>> GetAshbyJobsAsync(
        JobDiscoverySourceOptions source,
        CancellationToken cancellationToken)
    {
        var url =
            "https://api.ashbyhq.com/posting-api/job-board/"
            + Uri.EscapeDataString(source.BoardIdentifier)
            + "?includeCompensation=true";

        using var response = await httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);

        var jobs = new List<DiscoveredJobResult>();

        if (!document.RootElement.TryGetProperty("jobs", out var jobElements))
        {
            return jobs;
        }

        foreach (var job in jobElements.EnumerateArray())
        {
            var jobUrl = GetString(job, "jobUrl");
            var applyUrl = GetString(job, "applyUrl");

            jobs.Add(new DiscoveredJobResult
            {
                ExternalJobId = GetString(job, "id"),
                Source = "Ashby",
                CompanyName = source.CompanyName,
                PositionTitle = GetString(job, "title"),
                Location = GetString(job, "location"),
                Department = GetAshbyDepartment(job),
                JobDescription = FirstNonEmpty(
                    GetString(job, "descriptionPlain"),
                    CleanHtml(GetString(job, "descriptionHtml"))),
                JobPostingUrl = FirstNonEmpty(jobUrl, applyUrl),
                ApplyUrl = FirstNonEmpty(applyUrl, jobUrl),
                EmploymentType = GetString(job, "employmentType"),
                WorkplaceType = GetString(job, "workplaceType"),
                SalaryText = ReadAshbyCompensation(job),
                PostedDate = ParseDate(GetString(job, "publishedAt")),
                IsVerifiedSource = true
            });
        }

        return jobs;
    }

    private async Task<List<DiscoveredJobResult>> GetGreenhouseJobsAsync(
        JobDiscoverySourceOptions source,
        CancellationToken cancellationToken)
    {
        var url =
            "https://boards-api.greenhouse.io/v1/boards/"
            + Uri.EscapeDataString(source.BoardIdentifier)
            + "/jobs?content=true";

        using var response = await httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);

        var jobs = new List<DiscoveredJobResult>();

        if (!document.RootElement.TryGetProperty("jobs", out var jobElements))
        {
            return jobs;
        }

        foreach (var job in jobElements.EnumerateArray())
        {
            var locationText = string.Empty;
            if (job.TryGetProperty("location", out var locationElement))
            {
                locationText = GetString(locationElement, "name");
            }

            var jobUrl = GetString(job, "absolute_url");

            jobs.Add(new DiscoveredJobResult
            {
                ExternalJobId = GetValueAsString(job, "id"),
                Source = "Greenhouse",
                CompanyName = source.CompanyName,
                PositionTitle = GetString(job, "title"),
                Location = locationText,
                Department = ReadGreenhouseDepartments(job),
                JobDescription = CleanHtml(GetString(job, "content")),
                JobPostingUrl = jobUrl,
                ApplyUrl = jobUrl,
                PostedDate = ParseDate(FirstNonEmpty(
                    GetString(job, "updated_at"),
                    GetString(job, "created_at"))),
                IsVerifiedSource = true
            });
        }

        return jobs;
    }

    private async Task<List<DiscoveredJobResult>> GetLeverJobsAsync(
        JobDiscoverySourceOptions source,
        CancellationToken cancellationToken)
    {
        var url =
            "https://api.lever.co/v0/postings/"
            + Uri.EscapeDataString(source.BoardIdentifier)
            + "?mode=json";

        using var response = await httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);

        var jobs = new List<DiscoveredJobResult>();
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return jobs;
        }

        foreach (var job in document.RootElement.EnumerateArray())
        {
            var locationText = string.Empty;
            var employmentType = string.Empty;
            var department = string.Empty;

            if (job.TryGetProperty("categories", out var categories))
            {
                locationText = GetString(categories, "location");
                employmentType = GetString(categories, "commitment");
                department = FirstNonEmpty(
                    GetString(categories, "department"),
                    GetString(categories, "team"));
            }

            var hostedUrl = GetString(job, "hostedUrl");
            var applyUrl = GetString(job, "applyUrl");
            DateTime? postedDate = null;

            if (job.TryGetProperty("createdAt", out var createdAtElement)
                && createdAtElement.TryGetInt64(out var createdAtMilliseconds))
            {
                postedDate = DateTimeOffset
                    .FromUnixTimeMilliseconds(createdAtMilliseconds)
                    .UtcDateTime;
            }

            jobs.Add(new DiscoveredJobResult
            {
                ExternalJobId = GetString(job, "id"),
                Source = "Lever",
                CompanyName = source.CompanyName,
                PositionTitle = GetString(job, "text"),
                Location = locationText,
                Department = department,
                JobDescription = FirstNonEmpty(
                    GetString(job, "descriptionPlain"),
                    CleanHtml(GetString(job, "description"))),
                JobPostingUrl = FirstNonEmpty(hostedUrl, applyUrl),
                ApplyUrl = FirstNonEmpty(applyUrl, hostedUrl),
                EmploymentType = employmentType,
                WorkplaceType = GetString(job, "workplaceType"),
                PostedDate = postedDate,
                IsVerifiedSource = true
            });
        }

        return jobs;
    }

    private static void ApplySmartRecruitersDetails(
        DiscoveredJobResult job,
        JsonElement details)
    {
        var companyName = ReadNestedLabel(details, "company", "name");
        var location = ReadSmartRecruitersLocation(details);
        var department = FirstNonEmpty(
            ReadNestedLabel(details, "department", "label"),
            ReadNestedLabel(details, "function", "label"));
        var employmentType =
            ReadNestedLabel(details, "typeOfEmployment", "label");
        var experienceLevel =
            ReadNestedLabel(details, "experienceLevel", "label");
        var workplaceType = ReadSmartRecruitersWorkplaceType(details);

        job.CompanyName = FirstNonEmpty(companyName, job.CompanyName);
        job.PositionTitle = FirstNonEmpty(
            GetString(details, "name"),
            job.PositionTitle);
        job.Location = FirstNonEmpty(location, job.Location);
        job.Department = FirstNonEmpty(department, job.Department);
        job.EmploymentType = FirstNonEmpty(
            employmentType,
            job.EmploymentType);
        job.ExperienceLevel = FirstNonEmpty(
            experienceLevel,
            job.ExperienceLevel);
        job.WorkplaceType = FirstNonEmpty(
            workplaceType,
            job.WorkplaceType);
        job.PostedDate = ParseDate(GetString(details, "releasedDate"))
            ?? job.PostedDate;
        job.JobPostingUrl = FirstNonEmpty(
            GetString(details, "postingUrl"),
            job.JobPostingUrl);
        job.ApplyUrl = FirstNonEmpty(
            GetString(details, "applyUrl"),
            job.ApplyUrl,
            job.JobPostingUrl);

        if (details.TryGetProperty("jobAd", out var jobAd)
            && jobAd.ValueKind == JsonValueKind.Object
            && jobAd.TryGetProperty("sections", out var sections)
            && sections.ValueKind == JsonValueKind.Object)
        {
            var descriptionParts = new[]
            {
                BuildSmartRecruitersSection(
                    sections,
                    "companyDescription",
                    "About the company"),
                BuildSmartRecruitersSection(
                    sections,
                    "jobDescription",
                    "Job description"),
                BuildSmartRecruitersSection(
                    sections,
                    "qualifications",
                    "Qualifications"),
                BuildSmartRecruitersSection(
                    sections,
                    "additionalInformation",
                    "Additional information")
            };

            var fullDescription = string.Join(
                Environment.NewLine + Environment.NewLine,
                descriptionParts.Where(part =>
                    !string.IsNullOrWhiteSpace(part)));

            job.JobDescription = FirstNonEmpty(
                fullDescription,
                job.JobDescription);
        }
    }

    private static string ReadSmartRecruitersLocation(JsonElement job)
    {
        if (!job.TryGetProperty("location", out var location)
            || location.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        var country = GetString(location, "country");
        if (country.Equals("us", StringComparison.OrdinalIgnoreCase)
            || country.Equals("usa", StringComparison.OrdinalIgnoreCase))
        {
            country = "United States";
        }

        return string.Join(
            ", ",
            new[]
            {
                GetString(location, "city"),
                GetString(location, "region"),
                country
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string ReadSmartRecruitersWorkplaceType(JsonElement job)
    {
        var locationType = FirstNonEmpty(
            GetString(job, "locationType"),
            ReadNestedLabel(job, "locationType", "label"));

        if (!string.IsNullOrWhiteSpace(locationType))
        {
            return locationType;
        }

        if (job.TryGetProperty("location", out var location)
            && location.ValueKind == JsonValueKind.Object
            && location.TryGetProperty("remote", out var remote)
            && remote.ValueKind == JsonValueKind.True)
        {
            return "Remote";
        }

        return string.Empty;
    }

    private static string ReadNestedLabel(
        JsonElement element,
        string objectName,
        string propertyName)
    {
        return element.TryGetProperty(objectName, out var nested)
               && nested.ValueKind == JsonValueKind.Object
            ? GetString(nested, propertyName)
            : string.Empty;
    }

    private static string BuildSmartRecruitersSummary(
        string title,
        string companyName,
        string location,
        string department,
        string employmentType,
        string experienceLevel)
    {
        var facts = new[]
        {
            string.IsNullOrWhiteSpace(title)
                ? string.Empty
                : $"{title} at {companyName}.",
            string.IsNullOrWhiteSpace(location)
                ? string.Empty
                : $"Location: {location}",
            string.IsNullOrWhiteSpace(department)
                ? string.Empty
                : $"Department: {department}",
            string.IsNullOrWhiteSpace(employmentType)
                ? string.Empty
                : $"Employment type: {employmentType}",
            string.IsNullOrWhiteSpace(experienceLevel)
                ? string.Empty
                : $"Experience level: {experienceLevel}"
        };

        return string.Join(
            Environment.NewLine,
            facts.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string BuildSmartRecruitersPostingUrl(
        string companyIdentifier,
        string postingId,
        string title)
    {
        var slug = Regex.Replace(
                WebUtility.HtmlDecode(title ?? string.Empty),
                @"[^A-Za-z0-9]+",
                "-")
            .Trim('-');

        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = "job";
        }

        return "https://jobs.smartrecruiters.com/"
               + Uri.EscapeDataString(companyIdentifier)
               + "/"
               + Uri.EscapeDataString(postingId)
               + "-"
               + slug;
    }

    private static string BuildDescriptionSection(
        string heading,
        string html)
    {
        var text = CleanHtml(html);
        return string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : heading + Environment.NewLine + text;
    }

    private static string BuildSmartRecruitersSection(
        JsonElement sections,
        string sectionName,
        string fallbackHeading)
    {
        if (!sections.TryGetProperty(sectionName, out var section)
            || section.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        return BuildDescriptionSection(
            FirstNonEmpty(
                GetString(section, "title"),
                fallbackHeading),
            GetString(section, "text"));
    }

    private static int GetInteger(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        if (property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out var number))
        {
            return number;
        }

        return int.TryParse(property.ToString(), out number)
            ? number
            : 0;
    }

    private bool MatchesSearch(
        DiscoveredJobResult job,
        JobDiscoverySearchRequest request,
        SearchFilterContext filterContext)
    {
        job.RelevanceScore = searchEngine.Score(job, request.Keyword);

        if (!string.IsNullOrWhiteSpace(request.Keyword)
            && job.RelevanceScore == 0)
        {
            return false;
        }

        if (filterContext.LocationTerms.Count > 0
            && !filterContext.LocationTerms.Any(term =>
                Contains(job.Location, term)
                || (term.Equals("remote", StringComparison.OrdinalIgnoreCase)
                    && job.WorkplaceType.Equals(
                        "Remote",
                        StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        if (request.UnitedStatesOnly && !IsLikelyUnitedStatesJob(job))
        {
            return false;
        }

        if (filterContext.PostedCutoffUtc.HasValue)
        {
            if (!job.PostedDate.HasValue)
            {
                return false;
            }

            if (job.PostedDate.Value.ToUniversalTime()
                < filterContext.PostedCutoffUtc.Value)
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(request.EmploymentType)
            && !job.EmploymentType.Equals(
                request.EmploymentType,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.WorkplaceType)
            && !job.WorkplaceType.Equals(
                request.WorkplaceType,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (filterContext.ExperienceLevels.Count > 0
            && !filterContext.ExperienceLevels.Contains(job.ExperienceLevel))
        {
            return false;
        }

        return filterContext.SelectedSkills.Count == 0
               || filterContext.SelectedSkills.Any(
                   skill => searchEngine.Score(job, skill) > 0);
    }

    private static SearchFilterContext BuildFilterContext(
        JobDiscoverySearchRequest request)
    {
        var experienceLevels = request.ExperienceLevels
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(request.ExperienceLevel))
        {
            experienceLevels.Add(request.ExperienceLevel);
        }

        DateTime? postedCutoffUtc = request.PostedWithinDays.HasValue
            ? request.PostedWithinDays.Value == 1
                ? DateTime.UtcNow.AddHours(-24)
                : DateTime.UtcNow.AddDays(-request.PostedWithinDays.Value)
            : null;

        return new SearchFilterContext(
            SplitLocationTerms(request.Location),
            experienceLevels,
            request.Skills
                .Where(skill => !string.IsNullOrWhiteSpace(skill))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            postedCutoffUtc);
    }

    private static void NormalizeJob(DiscoveredJobResult job)
    {
        var combined = string.Join(
            " ",
            job.PositionTitle,
            job.Location,
            job.Department,
            job.JobDescription);

        job.EmploymentType = NormalizeEmploymentType(
            job.EmploymentType,
            combined);
        job.WorkplaceType = NormalizeWorkplaceType(
            job.WorkplaceType,
            combined);
        var inferredExperienceLevel = NormalizeExperienceLevel(
            job.PositionTitle,
            job.JobDescription);

        job.ExperienceLevel = inferredExperienceLevel == "Not specified"
            ? NormalizeProvidedExperienceLevel(job.ExperienceLevel)
            : inferredExperienceLevel;
    }

    private static string NormalizeExperienceLevel(
        string? title,
        string? description)
    {
        var titleText = title ?? string.Empty;
        var combined = $"{titleText} {description}";

        if (Regex.IsMatch(
                titleText,
                @"\b(intern|internship|co-op|apprentice)\b",
                RegexOptions.IgnoreCase))
        {
            return "Internship";
        }

        if (Regex.IsMatch(
                titleText,
                @"\b(manager|director|head|vice president|vp)\b",
                RegexOptions.IgnoreCase))
        {
            return "Manager";
        }

        if (Regex.IsMatch(
                titleText,
                @"\b(principal|staff|lead|architect|distinguished)\b",
                RegexOptions.IgnoreCase))
        {
            return "Lead / Principal";
        }

        if (Regex.IsMatch(
                titleText,
                @"\b(senior|sr\.?|level\s*[45]|iv|v)\b",
                RegexOptions.IgnoreCase)
            || Regex.IsMatch(
                combined,
                @"\b([5-9]|1[0-9])\+?\s*(years|yrs)\b",
                RegexOptions.IgnoreCase))
        {
            return "Senior";
        }

        if (Regex.IsMatch(
                titleText,
                @"\b(junior|jr\.?|entry|associate|new grad|graduate|level\s*1|\bi\b)\b",
                RegexOptions.IgnoreCase)
            || Regex.IsMatch(
                combined,
                @"\b(0\s*[-–]\s*2|1\s*[-–]\s*2)\s*(years|yrs)\b",
                RegexOptions.IgnoreCase))
        {
            return "Entry level";
        }

        if (Regex.IsMatch(
                titleText,
                @"\b(mid|intermediate|level\s*[23]|ii|iii)\b",
                RegexOptions.IgnoreCase)
            || Regex.IsMatch(
                combined,
                @"\b([2-4]\s*[-–]\s*[3-6]|[2-4]\+)\s*(years|yrs)\b",
                RegexOptions.IgnoreCase))
        {
            return "Mid level";
        }

        return "Not specified";
    }

    private static string NormalizeProvidedExperienceLevel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Equals("Any", StringComparison.OrdinalIgnoreCase))
        {
            return "Not specified";
        }

        if (Regex.IsMatch(value, @"\b(intern|internship|student)\b", RegexOptions.IgnoreCase))
        {
            return "Internship";
        }

        if (Regex.IsMatch(value, @"\b(entry|junior|graduate)\b", RegexOptions.IgnoreCase))
        {
            return "Entry level";
        }

        if (Regex.IsMatch(value, @"\b(mid|intermediate)\b", RegexOptions.IgnoreCase))
        {
            return "Mid level";
        }

        if (Regex.IsMatch(value, @"\b(manager|director|head)\b", RegexOptions.IgnoreCase))
        {
            return "Manager";
        }

        if (Regex.IsMatch(value, @"\b(lead|principal|staff|architect)\b", RegexOptions.IgnoreCase))
        {
            return "Lead / Principal";
        }

        return Regex.IsMatch(value, @"\b(senior|sr\.?|experienced)\b", RegexOptions.IgnoreCase)
            ? "Senior"
            : "Not specified";
    }

    private static string NormalizeEmploymentType(string? value, string text)
    {
        var candidate = $"{value} {text}";

        if (Regex.IsMatch(candidate, @"\b(intern|internship|co-op)\b", RegexOptions.IgnoreCase))
        {
            return "Internship";
        }

        if (Regex.IsMatch(candidate, @"\b(contract|contractor|temporary|temp)\b", RegexOptions.IgnoreCase))
        {
            return "Contract";
        }

        if (Regex.IsMatch(candidate, @"\bpart[ -]?time\b", RegexOptions.IgnoreCase))
        {
            return "Part time";
        }

        if (Regex.IsMatch(candidate, @"\b(full[ -]?time|permanent)\b", RegexOptions.IgnoreCase))
        {
            return "Full time";
        }

        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private static string NormalizeWorkplaceType(string? value, string text)
    {
        var candidate = $"{value} {text}";

        if (Regex.IsMatch(candidate, @"\bhybrid\b", RegexOptions.IgnoreCase))
        {
            return "Hybrid";
        }

        if (Regex.IsMatch(candidate, @"\b(remote|work from home|distributed)\b", RegexOptions.IgnoreCase))
        {
            return "Remote";
        }

        if (Regex.IsMatch(candidate, @"\b(on[ -]?site|in[ -]?office)\b", RegexOptions.IgnoreCase))
        {
            return "On-site";
        }

        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private static bool IsLikelyUnitedStatesJob(DiscoveredJobResult job)
    {
        var location = job.Location;
        if (string.IsNullOrWhiteSpace(location))
        {
            return false;
        }

        if (Regex.IsMatch(
                location,
                @"\b(United States|United States of America|USA|U\.S\.|US Remote|Remote US|Remote - US|North America)\b",
                RegexOptions.IgnoreCase))
        {
            return true;
        }

        return Regex.IsMatch(
            location,
            @"\b(Alabama|Alaska|Arizona|Arkansas|California|Colorado|Connecticut|Delaware|Florida|Georgia|Hawaii|Idaho|Illinois|Indiana|Iowa|Kansas|Kentucky|Louisiana|Maine|Maryland|Massachusetts|Michigan|Minnesota|Mississippi|Missouri|Montana|Nebraska|Nevada|New Hampshire|New Jersey|New Mexico|New York|North Carolina|North Dakota|Ohio|Oklahoma|Oregon|Pennsylvania|Rhode Island|South Carolina|South Dakota|Tennessee|Texas|Utah|Vermont|Virginia|Washington|West Virginia|Wisconsin|Wyoming|District of Columbia|AL|AK|AZ|AR|CA|CO|CT|DE|FL|GA|HI|ID|IL|IN|IA|KS|KY|LA|ME|MD|MA|MI|MN|MS|MO|MT|NE|NV|NH|NJ|NM|NY|NC|ND|OH|OK|OR|PA|RI|SC|SD|TN|TX|UT|VT|VA|WA|WV|WI|WY|DC)\b",
            RegexOptions.IgnoreCase);
    }

    private static List<string> SplitSearchTerms(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new List<string>();
        }

        return Regex.Matches(value, "[\\\"]([^\\\"]+)[\\\"]|([^,\\s]+)")
            .Select(match => FirstNonEmpty(
                match.Groups[1].Value,
                match.Groups[2].Value))
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .ToList();
    }

    private static List<string> SplitLocationTerms(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new List<string>();
        }

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(term => term.Trim())
            .Where(term => term.Length > 0)
            .ToList();
    }

    private static string GetDeduplicationKey(DiscoveredJobResult job)
    {
        if (!string.IsNullOrWhiteSpace(job.JobPostingUrl))
        {
            return job.JobPostingUrl.Trim().TrimEnd('/');
        }

        return string.Join(
            "|",
            job.CompanyName.Trim(),
            job.PositionTitle.Trim(),
            job.Location.Trim());
    }

    private static DiscoveredJobResult CloneJob(DiscoveredJobResult job)
    {
        return new DiscoveredJobResult
        {
            ExternalJobId = job.ExternalJobId,
            Source = job.Source,
            SourceIdentifier = job.SourceIdentifier,
            CompanyName = job.CompanyName,
            PositionTitle = job.PositionTitle,
            Location = job.Location,
            Department = job.Department,
            JobDescription = job.JobDescription,
            JobPostingUrl = job.JobPostingUrl,
            ApplyUrl = job.ApplyUrl,
            EmploymentType = job.EmploymentType,
            WorkplaceType = job.WorkplaceType,
            ExperienceLevel = job.ExperienceLevel,
            SalaryText = job.SalaryText,
            PostedDate = job.PostedDate,
            IsVerifiedSource = job.IsVerifiedSource
        };
    }

    private static string FriendlyError(Exception exception)
    {
        return exception is HttpRequestException httpException
            ? httpException.StatusCode.HasValue
                ? $"source returned HTTP {(int)httpException.StatusCode.Value}."
                : "source could not be reached."
            : "source could not be loaded.";
    }

    private static bool Contains(string? value, string searchValue)
    {
        return value?.Contains(
            searchValue.Trim(),
            StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Number => property.ToString(),
            _ => string.Empty
        };
    }

    private static string GetValueAsString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            ? property.ToString()
            : string.Empty;
    }

    private static string GetArrayText(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return string.Empty;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            return property.GetString() ?? string.Empty;
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Join(
            ", ",
            property.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
               ?? string.Empty;
    }

    private static DateTime? ParseDate(string? value)
    {
        return DateTime.TryParse(value, out var date) ? date : null;
    }

    private static string CleanHtml(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decoded = WebUtility.HtmlDecode(value);
        decoded = Regex.Replace(decoded, @"<\s*br\s*/?\s*>", "\n", RegexOptions.IgnoreCase);
        decoded = Regex.Replace(decoded, @"<\s*/\s*(p|li|h[1-6]|div)\s*>", "\n", RegexOptions.IgnoreCase);
        var withoutTags = Regex.Replace(decoded, "<[^>]+>", " ");

        return Regex.Replace(withoutTags, @"[ \t]+", " ")
            .Replace(" \n", "\n")
            .Trim();
    }

    private static string ReadAshbyCompensation(JsonElement job)
    {
        if (!job.TryGetProperty("compensation", out var compensation))
        {
            return string.Empty;
        }

        return compensation.ValueKind == JsonValueKind.String
            ? compensation.GetString() ?? string.Empty
            : compensation.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? compensation.ToString()
                : string.Empty;
    }

    private static string ReadJobicySalary(JsonElement job)
    {
        var minimum = GetValueAsString(job, "salaryMin");
        var maximum = GetValueAsString(job, "salaryMax");
        var currency = GetString(job, "salaryCurrency");
        var period = GetString(job, "salaryPeriod");

        if (string.IsNullOrWhiteSpace(minimum)
            && string.IsNullOrWhiteSpace(maximum))
        {
            return string.Empty;
        }

        var range = string.IsNullOrWhiteSpace(minimum)
            ? maximum
            : string.IsNullOrWhiteSpace(maximum)
                ? minimum
                : $"{minimum}–{maximum}";

        return string.Join(
            " ",
            new[] { currency, range, period }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static TimeSpan GetSourceCacheDuration(string? provider)
    {
        return provider?.Trim().ToLowerInvariant() switch
        {
            // Remotive requests no more than four refreshes per day.
            "remotive" => TimeSpan.FromHours(6),

            // Jobicy discourages polling more than once per hour.
            "jobicy" => TimeSpan.FromHours(1),

            _ => TimeSpan.FromMinutes(30)
        };
    }

    private static string GetAshbyDepartment(JsonElement job)
    {
        if (!job.TryGetProperty("department", out var department))
        {
            return string.Empty;
        }

        return department.ValueKind == JsonValueKind.String
            ? department.GetString() ?? string.Empty
            : GetString(department, "name");
    }

    private static string ReadGreenhouseDepartments(JsonElement job)
    {
        if (!job.TryGetProperty("departments", out var departments)
            || departments.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Join(
            ", ",
            departments.EnumerateArray()
                .Select(department => GetString(department, "name"))
                .Where(name => !string.IsNullOrWhiteSpace(name)));
    }

    private sealed record SourceLoadResult(
        List<DiscoveredJobResult> Jobs,
        string? Warning);

    private sealed record SearchFilterContext(
        IReadOnlyList<string> LocationTerms,
        IReadOnlySet<string> ExperienceLevels,
        IReadOnlyList<string> SelectedSkills,
        DateTime? PostedCutoffUtc);
}

public sealed class JobDiscoverySourceOptions
{
    public string CompanyName { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string BoardIdentifier { get; set; } = string.Empty;
    public int MaximumPages { get; set; } = 1;
    public bool Enabled { get; set; } = true;
}

public sealed class JobDiscoverySearchRequest
{
    public string Keyword { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public int? PostedWithinDays { get; set; }
    public string EmploymentType { get; set; } = string.Empty;
    public string WorkplaceType { get; set; } = string.Empty;
    public string ExperienceLevel { get; set; } = string.Empty;
    public List<string> ExperienceLevels { get; set; } = new();
    public List<string> Skills { get; set; } = new();
    public bool UnitedStatesOnly { get; set; } = true;
    public string SortOrder { get; set; } = "newest";
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public sealed class JobDiscoveryResponse
{
    public List<DiscoveredJobResult> Jobs { get; } = new();
    public List<string> Warnings { get; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public int TotalPages { get; set; } = 1;
    public int SourceCount { get; set; }
    public int SuccessfulSourceCount { get; set; }
    public int AllJobsLoaded { get; set; }
}

public sealed class DiscoveredJobResult
{
    public int RelevanceScore { get; set; }
    public string ExternalJobId { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string SourceIdentifier { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string PositionTitle { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string JobDescription { get; set; } = string.Empty;
    public string JobPostingUrl { get; set; } = string.Empty;
    public string ApplyUrl { get; set; } = string.Empty;
    public string EmploymentType { get; set; } = string.Empty;
    public string WorkplaceType { get; set; } = string.Empty;
    public string ExperienceLevel { get; set; } = string.Empty;
    public string SalaryText { get; set; } = string.Empty;
    public DateTime? PostedDate { get; set; }
    public bool IsVerifiedSource { get; set; }
}
