using System.Globalization;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace ApplicationTracker.Services;

public sealed class JobSearchEngine
{
    private readonly ConcurrentDictionary<string, IndexedJob> jobIndex =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, ParsedQuery> queryIndex =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex TokenRegex =
        new(@"[a-z0-9+#.]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> StopWords = new(
        new[]
        {
            "a", "an", "and", "at", "for", "in", "of", "on", "or", "the", "to", "with",
            "job", "jobs", "position", "positions", "role", "roles", "opening", "openings"
        },
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> GenericRoleTerms = new(
        new[]
        {
            "software", "engineer", "engineering", "developer", "development",
            "application", "applications", "senior", "junior", "lead", "principal",
            "manager", "specialist", "consultant", "associate"
        },
        StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, string[]> ConceptMap =
        BuildConceptMap();

    public int Score(DiscoveredJobResult job, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return 1;
        }

        var parsedQuery = queryIndex.GetOrAdd(query.Trim(), ParseQuery);
        if (parsedQuery.Terms.Count == 0 && parsedQuery.Phrases.Count == 0)
        {
            return 1;
        }

        var indexed = GetIndexedJob(job);
        var title = indexed.Title;
        var company = indexed.Company;
        var department = indexed.Department;
        var description = indexed.Description;
        var metadata = indexed.Metadata;

        var titleTokens = indexed.TitleTokens;
        var companyTokens = indexed.CompanyTokens;
        var departmentTokens = indexed.DepartmentTokens;
        var descriptionTokens = indexed.DescriptionTokens;
        var metadataTokens = indexed.MetadataTokens;

        var score = 0;
        var matchedRequiredConcepts = 0;
        var specificTerms = parsedQuery.Terms
            .Where(term => !GenericRoleTerms.Contains(term))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matchedSpecificConcepts = 0;

        foreach (var phrase in parsedQuery.Phrases)
        {
            var phraseScore = BestPhraseScore(
                phrase,
                title,
                company,
                department,
                description,
                metadata);

            if (phraseScore == 0)
            {
                return 0;
            }

            score += phraseScore;
        }

        foreach (var term in parsedQuery.Terms)
        {
            var alternatives = Expand(term);
            var termScore = alternatives.Max(alternative => BestTermScore(
                alternative,
                titleTokens,
                companyTokens,
                departmentTokens,
                descriptionTokens,
                metadataTokens));

            if (termScore > 0)
            {
                matchedRequiredConcepts++;
                if (specificTerms.Contains(term)) matchedSpecificConcepts++;
                score += termScore;
            }
        }

        if (parsedQuery.Terms.Count > 0 && matchedRequiredConcepts == 0)
        {
            return 0;
        }

        if (specificTerms.Count > 0 && matchedSpecificConcepts == 0)
        {
            return 0;
        }

        // Reward coverage without requiring every generic word to match.
        score += matchedRequiredConcepts * 8;

        var normalizedWholeQuery = Normalize(query.Trim(' ', '"'));
        if (title.Contains(normalizedWholeQuery, StringComparison.Ordinal))
        {
            score += 140;
        }
        else if (company.Contains(normalizedWholeQuery, StringComparison.Ordinal))
        {
            score += 110;
        }

        return score;
    }

    public void Prepare(IEnumerable<DiscoveredJobResult> jobs)
    {
        foreach (var job in jobs)
        {
            _ = GetIndexedJob(job);
        }
    }

    private IndexedJob GetIndexedJob(DiscoveredJobResult job)
    {
        var key = !string.IsNullOrWhiteSpace(job.JobPostingUrl)
            ? job.JobPostingUrl
            : $"{job.Source}:{job.ExternalJobId}:{job.PositionTitle}";

        return jobIndex.GetOrAdd(key, _ =>
        {
            var title = Normalize(job.PositionTitle);
            var company = Normalize(job.CompanyName);
            var department = Normalize(job.Department);
            var description = Normalize(job.JobDescription);
            var metadata = Normalize(string.Join(
                " ",
                job.Location,
                job.EmploymentType,
                job.WorkplaceType,
                job.ExperienceLevel,
                job.Source));

            return new IndexedJob(
                title,
                company,
                department,
                description,
                metadata,
                Tokenize(title),
                Tokenize(company),
                Tokenize(department),
                Tokenize(description),
                Tokenize(metadata));
        });
    }

    private static ParsedQuery ParseQuery(string query)
    {
        var phrases = Regex.Matches(query, "\"([^\"]+)\"")
            .Select(match => Normalize(match.Groups[1].Value))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var withoutPhrases = Regex.Replace(query, "\"[^\"]+\"", " ");
        var terms = Tokenize(Normalize(withoutPhrases))
            .Where(token => !StopWords.Contains(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ParsedQuery(terms, phrases);
    }

    private static int BestPhraseScore(
        string phrase,
        string title,
        string company,
        string department,
        string description,
        string metadata)
    {
        if (title.Contains(phrase, StringComparison.Ordinal)) return 160;
        if (company.Contains(phrase, StringComparison.Ordinal)) return 130;
        if (department.Contains(phrase, StringComparison.Ordinal)) return 90;
        if (description.Contains(phrase, StringComparison.Ordinal)) return 45;
        if (metadata.Contains(phrase, StringComparison.Ordinal)) return 20;
        return 0;
    }

    private static int BestTermScore(
        string term,
        IReadOnlySet<string> titleTokens,
        IReadOnlySet<string> companyTokens,
        IReadOnlySet<string> departmentTokens,
        IReadOnlySet<string> descriptionTokens,
        IReadOnlySet<string> metadataTokens)
    {
        if (titleTokens.Contains(term)) return 70;
        if (companyTokens.Contains(term)) return 60;
        if (departmentTokens.Contains(term)) return 45;
        if (descriptionTokens.Contains(term)) return 24;
        if (metadataTokens.Contains(term)) return 12;

        if (HasPrefixMatch(titleTokens, term)) return 42;
        if (HasPrefixMatch(companyTokens, term)) return 35;
        if (HasPrefixMatch(departmentTokens, term)) return 28;
        if (HasPrefixMatch(descriptionTokens, term)) return 14;

        // Typo tolerance is intentionally conservative to avoid unrelated jobs.
        if (term.Length >= 5)
        {
            if (HasFuzzyMatch(titleTokens, term)) return 26;
            if (HasFuzzyMatch(companyTokens, term)) return 22;
            if (HasFuzzyMatch(departmentTokens, term)) return 18;
            if (HasFuzzyMatch(descriptionTokens, term)) return 8;
        }

        return 0;
    }

    private static IEnumerable<string> Expand(string term)
    {
        var canonical = CanonicalizeToken(term);
        if (ConceptMap.TryGetValue(canonical, out var alternatives))
        {
            return alternatives;
        }

        return new[] { canonical };
    }

    private static bool HasPrefixMatch(IEnumerable<string> tokens, string term)
    {
        if (term.Length < 3) return false;

        return tokens.Any(token =>
            token.Length >= 3
            && (token.StartsWith(term, StringComparison.Ordinal)
                || term.StartsWith(token, StringComparison.Ordinal)));
    }

    private static bool HasFuzzyMatch(IEnumerable<string> tokens, string term)
    {
        var maximumDistance = term.Length >= 9 ? 2 : 1;

        return tokens.Any(token =>
            Math.Abs(token.Length - term.Length) <= maximumDistance
            && LevenshteinDistance(token, term, maximumDistance) <= maximumDistance);
    }

    private static int LevenshteinDistance(
        string left,
        string right,
        int maximumDistance)
    {
        if (Math.Abs(left.Length - right.Length) > maximumDistance)
        {
            return maximumDistance + 1;
        }

        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];

        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            current[0] = leftIndex;
            var rowMinimum = current[0];

            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                var substitutionCost =
                    left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1;

                current[rightIndex] = Math.Min(
                    Math.Min(
                        current[rightIndex - 1] + 1,
                        previous[rightIndex] + 1),
                    previous[rightIndex - 1] + substitutionCost);

                rowMinimum = Math.Min(rowMinimum, current[rightIndex]);
            }

            if (rowMinimum > maximumDistance)
            {
                return maximumDistance + 1;
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static HashSet<string> Tokenize(string value)
    {
        return TokenRegex.Matches(value)
            .Select(match => CanonicalizeToken(match.Value))
            .Where(token => token.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string CanonicalizeToken(string value)
    {
        var token = value.Trim().ToLowerInvariant();

        return token switch
        {
            "dotnet" or ".net" or "netcore" => ".net",
            "csharp" or "c#" => "c#",
            "js" => "javascript",
            "ts" => "typescript",
            "k8s" => "kubernetes",
            "ml" => "machine-learning",
            "ai" => "artificial-intelligence",
            "swe" or "sde" => "software-engineer",
            "qa" or "sdet" => "quality-assurance",
            _ => token
        };
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        value = Regex.Replace(value, @"\basp\s+dot\s+net\b", "asp.net", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\bdot\s*net\b", ".net", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\bc\s*sharp\b", "c#", RegexOptions.IgnoreCase);

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character)
                != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
    }

    private static IReadOnlyDictionary<string, string[]> BuildConceptMap()
    {
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        void Add(IEnumerable<string> queryTerms, params string[] alternatives)
        {
            var normalizedAlternatives = alternatives
                .Select(CanonicalizeToken)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var queryTerm in queryTerms.Select(CanonicalizeToken))
            {
                map[queryTerm] = normalizedAlternatives;
            }
        }

        Add(new[] { ".net", "c#", "asp.net", "blazor", "dotnet" },
            ".net", "c#", "asp.net", "blazor", "entity-framework");
        Add(new[] { "java", "spring", "spring-boot", "j2ee", "jakarta" },
            "java", "spring", "spring-boot", "j2ee", "jakarta");
        Add(new[] { "python", "django", "flask", "fastapi" },
            "python", "django", "flask", "fastapi");
        Add(new[] { "artificial-intelligence", "ai" },
            "artificial-intelligence", "ai", "machine", "learning", "llm", "nlp", "pytorch", "tensorflow");
        Add(new[] { "machine-learning", "ml" },
            "machine-learning", "machine", "learning", "ml", "pytorch", "tensorflow", "scikit-learn");
        Add(new[] { "data-science", "data-scientist" },
            "data", "scientist", "statistics", "analytics", "machine-learning");
        Add(new[] { "data-engineer", "data-engineering" },
            "data", "engineer", "etl", "spark", "databricks", "snowflake", "airflow");
        Add(new[] { "frontend", "front-end" },
            "frontend", "front-end", "react", "angular", "vue", "javascript", "typescript");
        Add(new[] { "backend", "back-end" },
            "backend", "back-end", "api", "microservices", ".net", "java", "python", "node.js");
        Add(new[] { "fullstack", "full-stack" },
            "fullstack", "full-stack", "frontend", "backend");
        Add(new[] { "javascript", "js" }, "javascript", "typescript", "node.js");
        Add(new[] { "typescript", "ts" }, "typescript", "javascript");
        Add(new[] { "react" }, "react", "react.js", "next.js");
        Add(new[] { "angular" }, "angular", "angular.js");
        Add(new[] { "devops", "sre", "site-reliability" },
            "devops", "sre", "site-reliability", "docker", "kubernetes", "terraform", "jenkins", "ci/cd");
        Add(new[] { "cloud", "cloud-engineer", "cloud-architect" },
            "cloud", "cloud-engineer", "cloud-architect", "azure", "aws", "gcp");
        Add(new[] { "azure" }, "azure", "microsoft-azure");
        Add(new[] { "aws" }, "aws", "amazon-web-services");
        Add(new[] { "gcp" }, "gcp", "google-cloud", "google-cloud-platform");
        Add(new[] { "quality-assurance", "qa", "sdet" },
            "quality-assurance", "qa", "sdet", "test", "testing", "automation", "selenium", "playwright", "cypress");
        Add(new[] { "business-analyst" },
            "business-analyst", "requirements", "stakeholder", "business-analysis");
        Add(new[] { "product-manager", "product-owner" },
            "product-manager", "product-management", "product-owner", "roadmap");
        Add(new[] { "security", "cybersecurity", "infosec" },
            "security", "cybersecurity", "infosec", "soc", "siem", "application-security");
        Add(new[] { "database", "dba" },
            "database", "dba", "sql", "sql-server", "postgresql", "mysql", "oracle");
        Add(new[] { "mobile" },
            "mobile", "ios", "android", "swift", "kotlin", "react-native", "flutter");
        Add(new[] { "embedded", "firmware" },
            "embedded", "firmware", "c", "c++", "rtos", "microcontroller");
        Add(new[] { "ui", "ux", "designer" },
            "ui", "ux", "designer", "figma", "user-experience", "product-design");

        return map;
    }

    private sealed record ParsedQuery(
        IReadOnlyList<string> Terms,
        IReadOnlyList<string> Phrases);

    private sealed record IndexedJob(
        string Title,
        string Company,
        string Department,
        string Description,
        string Metadata,
        IReadOnlySet<string> TitleTokens,
        IReadOnlySet<string> CompanyTokens,
        IReadOnlySet<string> DepartmentTokens,
        IReadOnlySet<string> DescriptionTokens,
        IReadOnlySet<string> MetadataTokens);
}
