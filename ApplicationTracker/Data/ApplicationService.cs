using ApplicationTracker.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace ApplicationTracker.Data;

public class ApplicationService(IDbContextFactory<ApplicationDbContext> factory)
{
    public async Task<List<JobApplication>> GetAllAsync(string? search = null, ApplicationStatus? status = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        var query = db.JobApplications.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchTokens = Regex
                .Matches(
                    search.ToLowerInvariant(),
                    @"[\p{L}\p{N}]+")
                .Select(match => match.Value)
                .Where(token => token.Length > 1)
                .Distinct()
                .Take(25)
                .ToList();

            foreach (var searchToken in searchTokens)
            {
                var token = searchToken;

                query = query.Where(x =>
                    x.CompanyName.ToLower().Contains(token)
                    || x.PositionTitle.ToLower().Contains(token)
                    || (x.Location != null
                        && x.Location.ToLower().Contains(token))
                    || (x.JobDescription != null
                        && x.JobDescription.ToLower().Contains(token))
                    || (x.JobPostingUrl != null
                        && x.JobPostingUrl.ToLower().Contains(token))
                    || (x.JobBoard != null
                        && x.JobBoard.ToLower().Contains(token))
                    || (x.Notes != null
                        && x.Notes.ToLower().Contains(token)));
            }
        }

        if (status.HasValue)
            query = query.Where(x => x.Status == status.Value);

        return await query.OrderByDescending(x => x.AppliedDate).ThenByDescending(x => x.Id).ToListAsync();
    }

    public async Task<JobApplication?> GetAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.JobApplications.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
    }

    public async Task SaveAsync(JobApplication item)
    {
        await using var db = await factory.CreateDbContextAsync();
        item.UpdatedAt = DateTime.UtcNow;
        if (item.Id == 0)
        {
            item.CreatedAt = DateTime.UtcNow;
            db.JobApplications.Add(item);
        }
        else
        {
            db.JobApplications.Update(item);
        }
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();
        var item = await db.JobApplications.FindAsync(id);
        if (item is null) return;
        db.JobApplications.Remove(item);
        await db.SaveChangesAsync();
    }

    public async Task<bool> UpdateStatusAsync(int applicationId, ApplicationStatus status)
    {
        await using var db = await factory.CreateDbContextAsync();

        var application = await db.JobApplications
            .FirstOrDefaultAsync(item => item.Id == applicationId);

        if (application is null)
        {
            return false;
        }

        application.Status = status;

        await db.SaveChangesAsync();

        return true;
    }

    public async Task<DashboardSummary> GetSummaryAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var weekStart = today.AddDays(-6);
        var applications = db.JobApplications.AsNoTracking();

        return new DashboardSummary(
            await applications.CountAsync(),
            await applications.CountAsync(x => x.AppliedDate == today),
            await applications.CountAsync(x => x.AppliedDate >= weekStart),
            await applications.CountAsync(x => x.Status == ApplicationStatus.Screening || x.Status == ApplicationStatus.Interview),
            await applications.GroupBy(x => x.AppliedDate)
                .OrderByDescending(x => x.Key)
                .Take(14)
                .Select(x => new DailyApplicationCount(x.Key, x.Count()))
                .ToListAsync());
    }
}

public record DashboardSummary(int Total, int Today, int LastSevenDays, int ActiveInterviews, List<DailyApplicationCount> DailyCounts);
public record DailyApplicationCount(DateOnly Date, int Count);
