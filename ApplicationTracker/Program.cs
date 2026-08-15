using ApplicationTracker.Components;
using ApplicationTracker.Data;
using ApplicationTracker.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
{
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection"));
});

builder.Services.AddScoped<ApplicationService>();

builder.Services.AddHttpClient<JobImportService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
});

builder.Services.AddSingleton<JobCaptureStore>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("ChromeExtension", policy =>
    {
        policy
            .SetIsOriginAllowed(origin =>
                origin.StartsWith(
                    "chrome-extension://",
                    StringComparison.OrdinalIgnoreCase))
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(
        "/Error",
        createScopeForErrors: true);

    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseCors("ChromeExtension");

app.UseStaticFiles();

app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapGet(
    "/resumes/{id:int}",
    async Task<IResult> (
        int id,
        IDbContextFactory<ApplicationDbContext> factory) =>
    {
        await using var db =
            await factory.CreateDbContextAsync();

        var application =
            await db.JobApplications
                .AsNoTracking()
                .FirstOrDefaultAsync(item =>
                    item.Id == id);

        if (application?.ResumeContent is not { Length: > 0 })
        {
            return Results.NotFound();
        }

        return Results.File(
            application.ResumeContent,
            application.ResumeContentType
                ?? "application/octet-stream",
            application.ResumeFileName
                ?? "resume");
    });

app.MapPost(
        "/api/job-captures",
        (
            JobCaptureRequest request,
            JobCaptureStore store) =>
        {
            if (string.IsNullOrWhiteSpace(request.Url)
                || string.IsNullOrWhiteSpace(request.RenderedText))
            {
                return Results.BadRequest(
                    "The captured job page was empty.");
            }

            var token = store.Add(request);

            return Results.Ok(new
            {
                token
            });
        })
    .RequireCors("ChromeExtension");

using (var scope = app.Services.CreateScope())
{
    var factory =
        scope.ServiceProvider
            .GetRequiredService<
                IDbContextFactory<ApplicationDbContext>>();

    await using var db =
        await factory.CreateDbContextAsync();

    await db.Database.EnsureCreatedAsync();
}

app.Run();