namespace ApplicationTracker.Services;

public sealed class JobDiscoveryState
{
    private readonly JobDiscoveryService discoveryService;
    private readonly JobSearchEngine searchEngine;
    private readonly SemaphoreSlim loadLock = new(1, 1);
    private Task? loadTask;
    private long searchVersion;

    public JobDiscoveryState(
        JobDiscoveryService discoveryService,
        JobSearchEngine searchEngine)
    {
        this.discoveryService = discoveryService;
        this.searchEngine = searchEngine;
    }

    public string Keyword { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string PostedWithinValue { get; set; } = string.Empty;
    public string EmploymentType { get; set; } = string.Empty;
    public string WorkplaceType { get; set; } = string.Empty;
    public string SortOrder { get; set; } = "newest";
    public bool UnitedStatesOnly { get; set; } = true;

    public HashSet<string> SelectedSkills { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> SelectedExperienceLevels { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public List<DiscoveredJobResult> CatalogJobs { get; private set; } = new();
    public List<DiscoveredJobResult> MatchingJobs { get; private set; } = new();
    public List<string> Warnings { get; private set; } = new();

    public int CurrentPage { get; private set; } = 1;
    public int PageSize { get; } = 20;
    public int TotalCount { get; private set; }
    public int TotalPages { get; private set; } = 1;
    public int SourceCount { get; private set; }
    public int SuccessfulSourceCount { get; private set; }
    public int AllJobsLoaded { get; private set; }

    public bool IsCatalogLoaded { get; private set; }
    public bool IsLoadingCatalog { get; private set; }
    public bool IsFiltering { get; private set; }
    public string? ErrorMessage { get; private set; }

    public IReadOnlyList<DiscoveredJobResult> CurrentJobs => MatchingJobs
        .Skip((CurrentPage - 1) * PageSize)
        .Take(PageSize)
        .ToList();

    public Task EnsureCatalogLoadedAsync()
    {
        if (IsCatalogLoaded)
        {
            return Task.CompletedTask;
        }

        return loadTask ??= LoadCatalogCoreAsync();
    }

    private async Task LoadCatalogCoreAsync()
    {
        await loadLock.WaitAsync();
        try
        {
            if (IsCatalogLoaded)
            {
                return;
            }

            IsLoadingCatalog = true;
            ErrorMessage = null;

            var response = await discoveryService.SearchAsync(
                new JobDiscoverySearchRequest
                {
                    UnitedStatesOnly = false,
                    Page = 1,
                    PageSize = 5000
                });

            CatalogJobs = response.Jobs;
            Warnings = response.Warnings;
            SourceCount = response.SourceCount;
            SuccessfulSourceCount = response.SuccessfulSourceCount;
            AllJobsLoaded = response.AllJobsLoaded;

            await Task.Run(() => searchEngine.Prepare(CatalogJobs));
            IsCatalogLoaded = true;
            await ApplyFiltersAsync();
        }
        catch (Exception)
        {
            ErrorMessage =
                "The job catalog could not be loaded. Please try again.";
            loadTask = null;
        }
        finally
        {
            IsLoadingCatalog = false;
            loadLock.Release();
        }
    }

    public async Task ApplyFiltersAsync()
    {
        if (!IsCatalogLoaded && CatalogJobs.Count == 0)
        {
            return;
        }

        var version = Interlocked.Increment(ref searchVersion);
        IsFiltering = true;
        ErrorMessage = null;

        var request = BuildRequest();
        var catalogSnapshot = CatalogJobs.ToList();
        var sourceInformation = new JobDiscoveryResponse
        {
            SourceCount = SourceCount,
            SuccessfulSourceCount = SuccessfulSourceCount,
            AllJobsLoaded = AllJobsLoaded
        };
        sourceInformation.Warnings.AddRange(Warnings);

        try
        {
            var response = await Task.Run(() =>
                discoveryService.FilterCatalog(
                    catalogSnapshot,
                    request,
                    sourceInformation));

            if (version != Volatile.Read(ref searchVersion))
            {
                return;
            }

            MatchingJobs = response.Jobs;
            TotalCount = response.TotalCount;
            TotalPages = Math.Max(
                1,
                (int)Math.Ceiling(TotalCount / (double)PageSize));
            CurrentPage = 1;
        }
        catch (Exception)
        {
            ErrorMessage =
                "The filters could not be applied. Please try again.";
        }
        finally
        {
            if (version == Volatile.Read(ref searchVersion))
            {
                IsFiltering = false;
            }
        }
    }

    public void GoToPage(int page)
    {
        CurrentPage = Math.Clamp(page, 1, TotalPages);
    }

    public void ClearFilters()
    {
        Keyword = string.Empty;
        Location = string.Empty;
        PostedWithinValue = string.Empty;
        EmploymentType = string.Empty;
        WorkplaceType = string.Empty;
        SortOrder = "newest";
        UnitedStatesOnly = true;
        SelectedSkills.Clear();
        SelectedExperienceLevels.Clear();
    }

    private JobDiscoverySearchRequest BuildRequest()
    {
        int? postedWithinDays = int.TryParse(
            PostedWithinValue,
            out var days)
                ? days
                : null;

        return new JobDiscoverySearchRequest
        {
            Keyword = Keyword,
            Location = Location,
            PostedWithinDays = postedWithinDays,
            EmploymentType = EmploymentType,
            WorkplaceType = WorkplaceType,
            ExperienceLevels = SelectedExperienceLevels.ToList(),
            Skills = SelectedSkills.ToList(),
            UnitedStatesOnly = UnitedStatesOnly,
            SortOrder = SortOrder,
            Page = 1,
            PageSize = 5000
        };
    }
}
