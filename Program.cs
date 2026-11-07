using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using RefertoPallamano_Blazor;
using RefertoPallamano_Blazor.Services;

namespace RefertoPallamano_Blazor;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebAssemblyHostBuilder.CreateDefault(args);
        builder.RootComponents.Add<App>("#app");
        builder.RootComponents.Add<HeadOutlet>("head::after");
        builder.Services.AddSingleton<GameService>();
        builder.Services.AddScoped(_ => new HttpClient());
        builder.Services.AddScoped<CloudArchiveService>();
        builder.Services.AddSingleton<ArchiveService>();
        await builder.Build().RunAsync();
    }
}
