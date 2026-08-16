using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ApplicationTracker.Services;

public sealed class JobDiscoveryService
{
    private readonly HttpClient httpClient;
    private readonly IConfiguration configuration;

    public JobDiscoveryService(
        HttpClient httpClient,
        IConfiguration configuration)
    {
        this.httpClient = httpClient;
        this.configuration = configuration;
    }

    public async Task<JobDiscoveryResponse> SearchAsync(
        string? keyword,
        string? location,
        CancellationToken cancellationToken = default)
    {
        var sources =
            configuration
                .GetSection("JobDiscovery:Sources")
                .Get<List<JobDiscoverySourceOptions>>()
            ?? new List<JobDiscoverySourceOptions>();

        var result = new JobDiscoveryResponse();

        foreach (var source in sources.Where(item => item.Enabled))
        {
            try
            {
                var sourceJobs =
                    source.Provider.ToLowerInvariant() switch
                    {
                        "ashby" => await GetAshbyJobsAsync(
                            source,
                            cancellationToken),

                        "greenhouse" => await GetGreenhouseJobsAsync(
                            source,
                            cancellationToken),

                        "lever" => await GetLeverJobsAsync(
                            source,
                            cancellationToken),

                        _ => new List<DiscoveredJobResult>()
                    };

                result.Jobs.AddRange(sourceJobs);
            }
            catch (Exception exception)
            {
                result.Warnings.Add(
                    $"{source.CompanyName}: {exception.Message}");
            }
        }

        var filteredJobs =
            result.Jobs
                .Where(job =>
                    MatchesSearch(
                        job,
                        keyword,
                        location))
                .GroupBy(
                    job =>
                        !string.IsNullOrWhiteSpace(job.ExternalJobId)
                            ? $"{job.Source}:{job.ExternalJobId}"
                            : job.JobPostingUrl,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderByDescending(job => job.PostedDate)
                .ThenBy(job => job.CompanyName)
                .ToList();

        result.Jobs.Clear();
        result.Jobs.AddRange(filteredJobs);

        return result;
    }

    private async Task<List<DiscoveredJobResult>>
        GetAshbyJobsAsync(
            JobDiscoverySourceOptions source,
            CancellationToken cancellationToken)
    {
        var url =
            "https://api.ashbyhq.com/posting-api/job-board/"
            + Uri.EscapeDataString(source.BoardIdentifier)
            + "?includeCompensation=true";

        using var response =
            await httpClient.GetAsync(
                url,
                cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var stream =
            await response.Content.ReadAsStreamAsync(
                cancellationToken);

        using var document =
            await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken);

        var jobs = new List<DiscoveredJobResult>();

        if (!document.RootElement.TryGetProperty(
                "jobs",
                out var jobElements))
        {
            return jobs;
        }

        foreach (var job in jobElements.EnumerateArray())
        {
            var jobUrl =
                GetString(job, "jobUrl");

            var applyUrl =
                GetString(job, "applyUrl");

            jobs.Add(
                new DiscoveredJobResult
                {
                    ExternalJobId =
                        GetString(job, "id"),

                    Source = "Ashby",

                    CompanyName =
                        source.CompanyName,

                    PositionTitle =
                        GetString(job, "title"),

                    Location =
                        GetString(job, "location"),

                    JobDescription =
                        FirstNonEmpty(
                            GetString(job, "descriptionPlain"),
                            CleanHtml(
                                GetString(job, "descriptionHtml"))),

                    JobPostingUrl =
                        FirstNonEmpty(jobUrl, applyUrl),

                    ApplyUrl =
                        FirstNonEmpty(applyUrl, jobUrl),

                    EmploymentType =
                        GetString(job, "employmentType"),

                    WorkplaceType =
                        GetString(job, "workplaceType"),

                    SalaryText =
                        ReadAshbyCompensation(job),

                    PostedDate =
                        ParseDate(
                            GetString(job, "publishedAt")),

                    IsVerifiedSource = true
                });
        }

        return jobs;
    }

    private async Task<List<DiscoveredJobResult>>
        GetGreenhouseJobsAsync(
            JobDiscoverySourceOptions source,
            CancellationToken cancellationToken)
    {
        var url =
            "https://boards-api.greenhouse.io/v1/boards/"
            + Uri.EscapeDataString(source.BoardIdentifier)
            + "/jobs?content=true";

        using var response =
            await httpClient.GetAsync(
                url,
                cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var stream =
            await response.Content.ReadAsStreamAsync(
                cancellationToken);

        using var document =
            await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken);

        var jobs = new List<DiscoveredJobResult>();

        if (!document.RootElement.TryGetProperty(
                "jobs",
                out var jobElements))
        {
            return jobs;
        }

        foreach (var job in jobElements.EnumerateArray())
        {
            var locationText = string.Empty;

            if (job.TryGetProperty(
                    "location",
                    out var locationElement))
            {
                locationText =
                    GetString(
                        locationElement,
                        "name");
            }

            var jobUrl =
                GetString(job, "absolute_url");

            jobs.Add(
                new DiscoveredJobResult
                {
                    ExternalJobId =
                        GetValueAsString(job, "id"),

                    Source = "Greenhouse",

                    CompanyName =
                        source.CompanyName,

                    PositionTitle =
                        GetString(job, "title"),

                    Location =
                        locationText,

                    JobDescription =
                        CleanHtml(
                            GetString(job, "content")),

                    JobPostingUrl =
                        jobUrl,

                    ApplyUrl =
                        jobUrl,

                    PostedDate =
                        ParseDate(
                            FirstNonEmpty(
                                GetString(job, "updated_at"),
                                GetString(job, "created_at"))),

                    IsVerifiedSource = true
                });
        }

        return jobs;
    }

    private async Task<List<DiscoveredJobResult>>
        GetLeverJobsAsync(
            JobDiscoverySourceOptions source,
            CancellationToken cancellationToken)
    {
        var url =
            "https://api.lever.co/v0/postings/"
            + Uri.EscapeDataString(source.BoardIdentifier)
            + "?mode=json";

        using var response =
            await httpClient.GetAsync(
                url,
                cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var stream =
            await response.Content.ReadAsStreamAsync(
                cancellationToken);

        using var document =
            await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken);

        var jobs = new List<DiscoveredJobResult>();

        if (document.RootElement.ValueKind
            != JsonValueKind.Array)
        {
            return jobs;
        }

        foreach (var job in document.RootElement.EnumerateArray())
        {
            var locationText = string.Empty;
            var employmentType = string.Empty;

            if (job.TryGetProperty(
                    "categories",
                    out var categories))
            {
                locationText =
                    GetString(
                        categories,
                        "location");

                employmentType =
                    GetString(
                        categories,
                        "commitment");
            }

            var hostedUrl =
                GetString(job, "hostedUrl");

            var applyUrl =
                GetString(job, "applyUrl");

            DateTime? postedDate = null;

            if (job.TryGetProperty(
                    "createdAt",
                    out var createdAtElement)
                && createdAtElement.TryGetInt64(
                    out var createdAtMilliseconds))
            {
                postedDate =
                    DateTimeOffset
                        .FromUnixTimeMilliseconds(
                            createdAtMilliseconds)
                        .UtcDateTime;
            }

            jobs.Add(
                new DiscoveredJobResult
                {
                    ExternalJobId =
                        GetString(job, "id"),

                    Source = "Lever",

                    CompanyName =
                        source.CompanyName,

                    PositionTitle =
                        GetString(job, "text"),

                    Location =
                        locationText,

                    JobDescription =
                        FirstNonEmpty(
                            GetString(job, "descriptionPlain"),
                            CleanHtml(
                                GetString(job, "description"))),

                    JobPostingUrl =
                        FirstNonEmpty(hostedUrl, applyUrl),

                    ApplyUrl =
                        FirstNonEmpty(applyUrl, hostedUrl),

                    EmploymentType =
                        employmentType,

                    WorkplaceType =
                        GetString(job, "workplaceType"),

                    PostedDate =
                        postedDate,

                    IsVerifiedSource = true
                });
        }

        return jobs;
    }

    private static bool MatchesSearch(
        DiscoveredJobResult job,
        string? keyword,
        string? location)
    {
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var keywordMatches =
                Contains(job.PositionTitle, keyword)
                || Contains(job.CompanyName, keyword)
                || Contains(job.JobDescription, keyword)
                || Contains(job.EmploymentType, keyword)
                || Contains(job.WorkplaceType, keyword);

            if (!keywordMatches)
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(location))
        {
            var locationMatches =
                Contains(job.Location, location)
                || (
                    location.Equals(
                        "remote",
                        StringComparison.OrdinalIgnoreCase)
                    && Contains(
                        job.WorkplaceType,
                        "remote"));

            if (!locationMatches)
            {
                return false;
            }
        }

        return true;
    }

    private static bool Contains(
        string? value,
        string searchValue)
    {
        return value?.Contains(
            searchValue.Trim(),
            StringComparison.OrdinalIgnoreCase)
            == true;
    }

    private static string GetString(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(
                propertyName,
                out var property))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String =>
                property.GetString()
                ?? string.Empty,

            JsonValueKind.Number =>
                property.ToString(),

            _ => string.Empty
        };
    }

    private static string GetValueAsString(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(
                propertyName,
                out var property))
        {
            return string.Empty;
        }

        return property.ToString();
    }

    private static string FirstNonEmpty(
        params string?[] values)
    {
        return values.FirstOrDefault(
                   value =>
                       !string.IsNullOrWhiteSpace(value))
               ?? string.Empty;
    }

    private static DateTime? ParseDate(
        string? value)
    {
        return DateTime.TryParse(
            value,
            out var date)
                ? date
                : null;
    }

    private static string CleanHtml(
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decoded =
            WebUtility.HtmlDecode(value);

        var withoutTags =
            Regex.Replace(
                decoded,
                "<[^>]+>",
                " ");

        return Regex.Replace(
                withoutTags,
                @"\s+",
                " ")
            .Trim();
    }

    private static string ReadAshbyCompensation(
        JsonElement job)
    {
        if (!job.TryGetProperty(
                "compensation",
                out var compensation))
        {
            return string.Empty;
        }

        if (compensation.ValueKind
            == JsonValueKind.String)
        {
            return compensation.GetString()
                   ?? string.Empty;
        }

        return compensation.ValueKind
               is JsonValueKind.Object
               or JsonValueKind.Array
            ? compensation.ToString()
            : string.Empty;
    }
}

public sealed class JobDiscoverySourceOptions
{
    public string CompanyName { get; set; }
        = string.Empty;

    public string Provider { get; set; }
        = string.Empty;

    public string BoardIdentifier { get; set; }
        = string.Empty;

    public bool Enabled { get; set; }
        = true;
}

public sealed class JobDiscoveryResponse
{
    public List<DiscoveredJobResult> Jobs { get; }
        = new();

    public List<string> Warnings { get; }
        = new();
}

public sealed class DiscoveredJobResult
{
    public string ExternalJobId { get; set; }
        = string.Empty;

    public string Source { get; set; }
        = string.Empty;

    public string CompanyName { get; set; }
        = string.Empty;

    public string PositionTitle { get; set; }
        = string.Empty;

    public string Location { get; set; }
        = string.Empty;

    public string JobDescription { get; set; }
        = string.Empty;

    public string JobPostingUrl { get; set; }
        = string.Empty;

    public string ApplyUrl { get; set; }
        = string.Empty;

    public string EmploymentType { get; set; }
        = string.Empty;

    public string WorkplaceType { get; set; }
        = string.Empty;

    public string SalaryText { get; set; }
        = string.Empty;

    public DateTime? PostedDate { get; set; }

    public bool IsVerifiedSource { get; set; }
}