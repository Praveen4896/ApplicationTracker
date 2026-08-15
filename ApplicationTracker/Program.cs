using ApplicationTracker.Components;
using ApplicationTracker.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddScoped<ApplicationService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapGet("/resumes/{id:int}", async Task<IResult> (int id, IDbContextFactory<ApplicationDbContext> factory) =>
{
    await using var db = await factory.CreateDbContextAsync();
    var item = await db.JobApplications.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
    if (item?.ResumeContent is not { Length: > 0 })
        return Results.NotFound();

    return Results.File(item.ResumeContent, item.ResumeContentType ?? "application/octet-stream", item.ResumeFileName ?? "resume");
});

using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();
}

app.Run();
