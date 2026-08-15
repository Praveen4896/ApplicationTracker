using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ApplicationTracker.Services;

public class JobImportService(HttpClient httpClient)
{
    public async Task<JobImportResult> ImportAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var requestedUri)
            || (requestedUri.Scheme != Uri.UriSchemeHttps
                && requestedUri.Scheme != Uri.UriSchemeHttp))
        {
            return JobImportResult.Failed(
                "Please enter a valid public job-posting URL.");
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                requestedUri);

            request.Headers.UserAgent.ParseAdd(
                "Mozilla/5.0 ApplicationTracker/1.0");

            using var response = await httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                return JobImportResult.Failed(
                    $"The job page returned status code {(int)response.StatusCode}.");
            }

            var html = await response.Content.ReadAsStringAsync();

            var resolvedUrl =
                response.RequestMessage?.RequestUri?.ToString() ?? url;

            var resolvedUri =
                response.RequestMessage?.RequestUri ?? requestedUri;

            var result = ReadStructuredJobPosting(html);

            var pageTitle = GetHtmlTitle(html);
            var metaTitle = GetMetaContent(html, "og:title");
            var metaDescription = GetMetaContent(html, "description")
                ?? GetMetaContent(html, "og:description");

            var title = result.Title;

            if (string.IsNullOrWhiteSpace(title))
            {
                title = GetPositionFromTitle(metaTitle ?? pageTitle);
            }

            var company = result.CompanyName;

            if (string.IsNullOrWhiteSpace(company))
            {
                company = GetCompanyFromTitle(metaTitle ?? pageTitle);
            }

            var description = result.Description;

            if (string.IsNullOrWhiteSpace(description))
            {
                description = metaDescription;
            }

            var source = GetSourceName(resolvedUri.Host);

            var jobId = GetQueryValue(resolvedUri, "joblistid")
                ?? GetQueryValue(resolvedUri, "jobId")
                ?? GetQueryValue(resolvedUri, "jobid")
                ?? GetQueryValue(resolvedUri, "jr_id");

            if (!string.IsNullOrWhiteSpace(jobId))
            {
                description = string.IsNullOrWhiteSpace(description)
                    ? $"Job ID: {jobId}"
                    : $"Job ID: {jobId}{Environment.NewLine}"
                      + $"Source: {source}{Environment.NewLine}"
                      + Environment.NewLine
                      + description;
            }

            if (string.IsNullOrWhiteSpace(title)
                && string.IsNullOrWhiteSpace(company)
                && string.IsNullOrWhiteSpace(description))
            {
                return JobImportResult.Failed(
                    "The page opened, but its job details could not be read. "
                    + "You can still enter the information manually.");
            }

            return new JobImportResult
            {
                Success = true,
                PositionTitle = title,
                CompanyName = company,
                Location = result.Location,
                JobDescription = description,
                JobPostingUrl = resolvedUrl,
                JobBoard = source,
                JobId = jobId
            };
        }
        catch (TaskCanceledException)
        {
            return JobImportResult.Failed(
                "The job page took too long to respond.");
        }
        catch (HttpRequestException)
        {
            return JobImportResult.Failed(
                "The job page could not be reached. "
                + "The website may be blocking automatic imports.");
        }
        catch (Exception)
        {
            return JobImportResult.Failed(
                "The job details could not be imported.");
        }
    }

    private static StructuredJob ReadStructuredJobPosting(string html)
    {
        var matches = Regex.Matches(
            html,
            @"<script[^>]*type\s*=\s*[""']application/ld\+json[""'][^>]*>(.*?)</script>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match match in matches)
        {
            try
            {
                var json = WebUtility.HtmlDecode(match.Groups[1].Value);

                using var document = JsonDocument.Parse(json);

                var jobPosting = FindJobPosting(document.RootElement);

                if (!jobPosting.HasValue)
                {
                    continue;
                }

                var job = jobPosting.Value;

                return new StructuredJob
                {
                    Title = GetString(job, "title"),
                    Description = CleanHtml(
                        GetString(job, "description")),
                    CompanyName = GetNestedString(
                        job,
                        "hiringOrganization",
                        "name"),
                    Location = GetLocation(job)
                };
            }
            catch (JsonException)
            {
                // Ignore invalid structured data and try the next block.
            }
        }

        return new StructuredJob();
    }

    private static JsonElement? FindJobPosting(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("@type", out var type)
                && type.ValueKind == JsonValueKind.String
                && string.Equals(
                    type.GetString(),
                    "JobPosting",
                    StringComparison.OrdinalIgnoreCase))
            {
                return element;
            }

            foreach (var property in element.EnumerateObject())
            {
                var result = FindJobPosting(property.Value);

                if (result.HasValue)
                {
                    return result;
                }
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var result = FindJobPosting(item);

                if (result.HasValue)
                {
                    return result;
                }
            }
        }

        return null;
    }

    private static string? GetLocation(JsonElement job)
    {
        if (!job.TryGetProperty("jobLocation", out var location))
        {
            return null;
        }

        if (location.ValueKind == JsonValueKind.Array)
        {
            location = location.EnumerateArray().FirstOrDefault();
        }

        if (location.ValueKind != JsonValueKind.Object
            || !location.TryGetProperty("address", out var address))
        {
            return null;
        }

        if (address.ValueKind == JsonValueKind.String)
        {
            return address.GetString();
        }

        if (address.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var parts = new[]
        {
            GetString(address, "addressLocality"),
            GetString(address, "addressRegion"),
            GetString(address, "addressCountry")
        };

        var locationText = string.Join(
            ", ",
            parts.Where(x => !string.IsNullOrWhiteSpace(x)));

        return string.IsNullOrWhiteSpace(locationText)
            ? null
            : locationText;
    }

    private static string? GetNestedString(
        JsonElement element,
        string objectName,
        string propertyName)
    {
        if (!element.TryGetProperty(objectName, out var child)
            || child.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return GetString(child, propertyName);
    }

    private static string? GetString(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return WebUtility.HtmlDecode(property.GetString());
    }

    private static string? GetMetaContent(
        string html,
        string metaName)
    {
        var escapedName = Regex.Escape(metaName);

        var pattern =
            $@"<meta[^>]*(?:name|property)\s*=\s*[""']{escapedName}[""'][^>]*content\s*=\s*[""'](?<value>.*?)[""'][^>]*>";

        var match = Regex.Match(
            html,
            pattern,
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (!match.Success)
        {
            pattern =
                $@"<meta[^>]*content\s*=\s*[""'](?<value>.*?)[""'][^>]*(?:name|property)\s*=\s*[""']{escapedName}[""'][^>]*>";

            match = Regex.Match(
                html,
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        return match.Success
            ? WebUtility.HtmlDecode(match.Groups["value"].Value).Trim()
            : null;
    }

    private static string? GetHtmlTitle(string html)
    {
        var match = Regex.Match(
            html,
            @"<title[^>]*>(?<value>.*?)</title>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        return match.Success
            ? CleanHtml(match.Groups["value"].Value)
            : null;
    }

    private static string? GetPositionFromTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var separators = new[] { " @ ", " | ", " - " };

        foreach (var separator in separators)
        {
            var parts = value.Split(
                separator,
                2,
                StringSplitOptions.TrimEntries);

            if (parts.Length == 2)
            {
                return parts[0];
            }
        }

        return value.Trim();
    }

    private static string? GetCompanyFromTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.Contains(" @ "))
        {
            return value.Split(
                " @ ",
                2,
                StringSplitOptions.TrimEntries)[1]
                .Replace("| Jobright.ai", string.Empty)
                .Trim();
        }

        return null;
    }

    private static string? GetQueryValue(
        Uri uri,
        string key)
    {
        var query = uri.Query.TrimStart('?');

        foreach (var pair in query.Split(
                     '&',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);

            if (parts.Length == 2
                && string.Equals(
                    Uri.UnescapeDataString(parts[0]),
                    key,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(parts[1]);
            }
        }

        return null;
    }

    private static string GetSourceName(string host)
    {
        if (host.Contains(
                "hirebridge",
                StringComparison.OrdinalIgnoreCase))
        {
            return "Hirebridge via Jobright";
        }

        if (host.Contains(
                "jobright",
                StringComparison.OrdinalIgnoreCase))
        {
            return "Jobright";
        }

        return host
            .Replace("www.", string.Empty)
            .Trim();
    }

    private static string? CleanHtml(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = Regex.Replace(
            value,
            @"<\s*br\s*/?\s*>",
            Environment.NewLine,
            RegexOptions.IgnoreCase);

        text = Regex.Replace(
            text,
            @"</\s*(p|div|li|h1|h2|h3|h4)\s*>",
            Environment.NewLine,
            RegexOptions.IgnoreCase);

        text = Regex.Replace(text, "<.*?>", string.Empty);

        text = WebUtility.HtmlDecode(text);

        text = Regex.Replace(
            text,
            @"[ \t]+",
            " ");

        text = Regex.Replace(
            text,
            @"(\r?\n){3,}",
            Environment.NewLine + Environment.NewLine);

        return text.Trim();
    }

    private sealed class StructuredJob
    {
        public string? Title { get; init; }
        public string? CompanyName { get; init; }
        public string? Location { get; init; }
        public string? Description { get; init; }
    }
}

public class JobImportResult
{
    public bool Success { get; init; }
    public string? CompanyName { get; init; }
    public string? PositionTitle { get; init; }
    public string? Location { get; init; }
    public string? JobDescription { get; init; }
    public string? JobPostingUrl { get; init; }
    public string? JobBoard { get; init; }
    public string? JobId { get; init; }
    public string? ErrorMessage { get; init; }

    public static JobImportResult Failed(string message)
    {
        return new JobImportResult
        {
            Success = false,
            ErrorMessage = message
        };
    }
}