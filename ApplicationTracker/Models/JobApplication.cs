using System.ComponentModel.DataAnnotations;

namespace ApplicationTracker.Models;

public class JobApplication
{
    public int Id { get; set; }

    [Required, StringLength(150)]
    public string CompanyName { get; set; } = string.Empty;

    [Required, StringLength(150)]
    public string PositionTitle { get; set; } = string.Empty;

    [Url, StringLength(1000)]
    public string? JobPostingUrl { get; set; }

    [StringLength(100)]
    public string? JobBoard { get; set; }

    [StringLength(150)]
    public string? Location { get; set; }

    public string? JobDescription { get; set; }

    [StringLength(260)]
    public string? ResumeFileName { get; set; }

    public byte[]? ResumeContent { get; set; }

    [StringLength(100)]
    public string? ResumeContentType { get; set; }

    [Required]
    public DateOnly AppliedDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);

    public ApplicationStatus Status { get; set; } = ApplicationStatus.Applied;

    [StringLength(150)]
    public string? ContactName { get; set; }

    [EmailAddress, StringLength(254)]
    public string? ContactEmail { get; set; }

    public DateTime? NextStepDate { get; set; }

    [StringLength(2000)]
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
