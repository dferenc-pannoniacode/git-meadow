using GitMeadow.Components;
using GitMeadow.Git;
using GitMeadow.Workbench;

var builder = WebApplication.CreateBuilder(args);
var root = builder.Configuration["GitMeadow:Workspace"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "workspace");

builder.Services.AddSingleton(new GitWorkspace(root));
builder.Services.AddSingleton(_ => new GitSettings(GitSettings.ResolveHome()));
builder.Services.AddSingleton<SampleRepository>();
builder.Services.AddRazorComponents().AddInteractiveServerComponents()
    .AddHubOptions(options => options.MaximumReceiveMessageSize = 8 * 1024 * 1024);

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.Use(async (context, next) =>
{
    // Browser-originated SignalR traffic must come from this instance.
    if (context.Request.Path.StartsWithSegments("/_blazor") &&
        context.Request.Headers.TryGetValue("Origin", out var origin) &&
        (!Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var source) ||
         source.Scheme != context.Request.Scheme ||
         !string.Equals(source.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();
app.MapStaticAssets();
app.MapGet("/healthz", () => Results.Ok("ready"));
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
