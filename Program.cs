
using Microsoft.AspNetCore.Authentication.Cookies;
using UACloudAction;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Auth";
        options.AccessDeniedPath = "/Shared/Error";
        options.ExpireTimeSpan = TimeSpan.FromHours(1);
    });

builder.Services.AddAuthorization();

builder.Services.AddSingleton<ActionProcessor>();

var app = builder.Build();

app.UseHsts();

app.UseHttpsRedirection();

app.UseForwardedHeaders();

app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();

app.UseAuthorization();

_ = Task.Run(() => app.Services.GetService<ActionProcessor>()?.Run());

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
