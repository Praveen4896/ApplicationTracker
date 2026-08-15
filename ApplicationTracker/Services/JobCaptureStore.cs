using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace ApplicationTracker.Services;

public sealed class JobCaptureStore
{
    private readonly ConcurrentDictionary<
        string,
        CapturedJob> captures = new();

    public string Add(
        JobCaptureRequest request)
    {
        var token =
            Guid.NewGuid().ToString("N");

        captures[token] =
            BuildCapturedJob(request);

        var cutoff =
            DateTime.UtcNow.AddHours(-1);

        var expiredTokens = captures
            .Where(
                item =>
                    item.Value.CapturedAtUtc
                    < cutoff)
            .Select(item => item.Key)
            .ToList();

        foreach (var expiredToken
                 in expiredTokens)
        {
            captures.TryRemove(
                expiredToken,
                out _);
        }

        return token;
    }

    public CapturedJob? Get(string token)
    {
        return captures.TryGetValue(
            token,
            out var capture)
                ? capture
                : null;
    }

    private static CapturedJob
        BuildCapturedJob(
            JobCaptureRequest request)
    {
        var text =
            Normalize(request.RenderedText);

        var company =
            Clean(request.CompanyName);

        var title =
            Clean(request.PositionTitle);

        var location =
            Clean(request.Location);

        var jobId =
            Clean(request.JobId);

        if (string.IsNullOrWhiteSpace(company))
        {
            company = Match(
                text,
                @"(?:^|\n)About\s+([^\n]+)");
        }

        if (string.IsNullOrWhiteSpace(company))
        {
            company = Match(
                text,
                @"(?:^|\n)At\s+([^,\n]{2,100}),\s+we\b");
        }

        if (string.IsNullOrWhiteSpace(jobId))
        {
            jobId = Match(
                text,
                @"(?:Job Req Id|Job Req ID|Job number|Job ID|Requisition ID|Req ID)\s*:?\s*\n?\s*([A-Za-z0-9_-]+)");
        }

        if (
            string.IsNullOrWhiteSpace(
                location)
            && !string.IsNullOrWhiteSpace(
                title))
        {
            var titleIndex =
                text.IndexOf(
                    title,
                    StringComparison
                        .OrdinalIgnoreCase);

            if (titleIndex >= 0)
            {
                var afterTitle =
                    text[
                        (titleIndex
                         + title.Length)..]
                    .TrimStart();

                location = afterTitle
                    .Split(
                        '\n',
                        StringSplitOptions
                            .RemoveEmptyEntries)
                    .FirstOrDefault()
                    ?.Trim();

                if (
                    location is "Actions"
                    or "Apply"
                    or "Job Description"
                    or "Overview"
                    or "Application")
                {
                    location = null;
                }
            }
        }

        var description =
            ExtractDescription(
                text,
                request.Source,
                title);

        var header =
            new List<string>();

        var descriptionAlreadyContainsJobId =
            Regex.IsMatch(
                description,
                @"(?im)^\s*Job\s+(?:Req\s+Id|Req\s+ID|number|ID)\s*:?\s*$");

        if (
            !string.IsNullOrWhiteSpace(
                jobId)
            && !descriptionAlreadyContainsJobId)
        {
            header.Add(
                $"Job ID: {jobId}");
        }

        if (
            !string.IsNullOrWhiteSpace(
                request.Source))
        {
            header.Add(
                $"Source: {request.Source}");
        }

        if (header.Count > 0)
        {
            description =
                string.Join(
                    Environment.NewLine,
                    header)
                + Environment.NewLine
                + Environment.NewLine
                + description;
        }

        return new CapturedJob
        {
            CompanyName = company,
            PositionTitle = title,
            Location = location,
            JobId = jobId,
            JobBoard =
                Clean(request.Source),
            JobPostingUrl =
                Clean(request.Url),
            JobDescription =
                description,
            CapturedAtUtc =
                DateTime.UtcNow
        };
    }

    private static string
        ExtractDescription(
            string text,
            string? source,
            string? positionTitle)
    {
        string[] startMarkers;


        if (source?.Contains(
                "linkedin",
                StringComparison
            .OrdinalIgnoreCase)
            == true)
        {
            startMarkers = new[]
            {
                "About the job"
            };
        }
        else

            if (
            source?.Contains(
                "ashby",
                StringComparison
                    .OrdinalIgnoreCase)
            == true)
        {
            startMarkers =
                string.IsNullOrWhiteSpace(
                    positionTitle)
                    ? new[]
                    {
                        "Location",
                        "Overview"
                    }
                    : new[]
                    {
                        positionTitle,
                        "Location",
                        "Overview"
                    };
        }
        else if (
            source?.Contains(
                "citi",
                StringComparison
                    .OrdinalIgnoreCase)
            == true)
        {
            startMarkers = new[]
            {
                "Job Req Id",
                "Discover your future at Citi",
                "Job Overview"
            };
        }
        else
        {
            startMarkers = new[]
            {
                "Job number",
                "Job Req Id",
                "Job Req ID",
                "Requisition ID",
                "Req ID",
                "Job Description",
                "Job description",
                "Overview"
            };
        }

        var startIndex = -1;

        foreach (var marker
                 in startMarkers)
        {
            var markerMatch =
                Regex.Match(
                    text,
                    $@"(?im)^[ \t]*{Regex.Escape(marker)}[ \t]*:?[ \t]*$");

            if (markerMatch.Success)
            {
                startIndex =
                    markerMatch.Index;

                break;
            }
        }

        var description =
            startIndex >= 0
                ? text[startIndex..]
                    .Trim()
                : text.Trim();

        var endMarkers =
    source?.Contains(
        "linkedin",
        StringComparison.OrdinalIgnoreCase)
    == true
        ? new[]
        {
            "About the company",
            "Set alert for similar jobs",
            "Similar jobs"
        }
        : new[]
        {
            "Apply for this Job",
            "Submit Application",
            "Powered by",
            "Share this job",
            "Explore More Jobs",
            "Explore more jobs"
        };

        var endIndex = -1;

        foreach (var marker
                 in endMarkers)
        {
            var markerMatch =
                Regex.Match(
                    description,
                    $@"(?im)^[ \t]*{Regex.Escape(marker)}[ \t]*$");

            if (
                markerMatch.Success
                && (
                    endIndex < 0
                    || markerMatch.Index
                    < endIndex))
            {
                endIndex =
                    markerMatch.Index;
            }
        }

        if (endIndex >= 0)
        {
            description =
                description[..endIndex]
                    .Trim();
        }

        return description;
    }

    private static string Normalize(
        string? value)
    {
        var text =
            (value ?? string.Empty)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n');

        return Regex.Replace(
                text,
                @"\n{3,}",
                "\n\n")
            .Trim();
    }

    private static string? Clean(
        string? value)
    {
        return string.IsNullOrWhiteSpace(
            value)
                ? null
                : value.Trim();
    }

    private static string? Match(
        string text,
        string pattern)
    {
        var match =
            Regex.Match(
                text,
                pattern,
                RegexOptions.IgnoreCase);

        return match.Success
            ? match.Groups[1]
                .Value
                .Trim()
            : null;
    }
}

public sealed class JobCaptureRequest
{
    public string? Url { get; set; }

    public string? PageTitle
    {
        get;
        set;
    }

    public string? PositionTitle
    {
        get;
        set;
    }

    public string? CompanyName
    {
        get;
        set;
    }

    public string? Location
    {
        get;
        set;
    }

    public string? JobId
    {
        get;
        set;
    }

    public string? Source
    {
        get;
        set;
    }

    public string? RenderedText
    {
        get;
        set;
    }

    public DateTime? CapturedAtUtc
    {
        get;
        set;
    }
}

public sealed class CapturedJob
{
    public string? CompanyName
    {
        get;
        init;
    }

    public string? PositionTitle
    {
        get;
        init;
    }

    public string? Location
    {
        get;
        init;
    }

    public string? JobId
    {
        get;
        init;
    }

    public string? JobBoard
    {
        get;
        init;
    }

    public string? JobPostingUrl
    {
        get;
        init;
    }

    public string JobDescription
    {
        get;
        init;
    } = string.Empty;

    public DateTime CapturedAtUtc
    {
        get;
        init;
    }
}