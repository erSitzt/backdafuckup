using Backdafuckup.Web;
using Backdafuckup.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddMudServices();
builder.Services.AddScoped(_ =>
{
    var http = new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) };
    http.DefaultRequestHeaders.Add(ApiHeadersValue.Name, "1");
    return http;
});
builder.Services.AddScoped<ApiClient>();
builder.Services.AddScoped<AuthState>();
builder.Services.AddScoped<LiveConnection>();

await builder.Build().RunAsync();
