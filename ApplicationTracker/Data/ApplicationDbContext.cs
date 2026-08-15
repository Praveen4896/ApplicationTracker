using ApplicationTracker.Models;
using Microsoft.EntityFrameworkCore;

namespace ApplicationTracker.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public DbSet<JobApplication> JobApplications => Set<JobApplication>();
}
