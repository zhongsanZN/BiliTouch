using System.Windows;
using System.Windows.Media;

namespace BiliTouch.Services;

public sealed record FontChoice(string Id,string Name);

public static class AppTypography
{
    public const string ResourceKey="AppFontFamily";
    public static readonly FontChoice[] Choices={new("System","系统默认"),new("HarmonyOS","HarmonyOS Sans"),new("JingNan","荆南波波黑")};
    public static event Action? Changed;
    public static string Normalize(string? id)=>Choices.Any(c=>c.Id==id)?id!:"System";
    public static FontFamily Family(string? id)=>Normalize(id) switch
    {
        "HarmonyOS"=>new(new Uri("pack://application:,,,/BiliTouch;component/Assets/Fonts/"),"./#HarmonyOS Sans SC"),
        "JingNan"=>new(new Uri("pack://application:,,,/BiliTouch;component/Assets/Fonts/"),"./#荆南波波黑"),
        _=>new("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI, Microsoft YaHei")
    };
    public static void Apply(string id)
    {
        if(Application.Current is not{} app)return;
        app.Resources[ResourceKey]=Family(id);
        Changed?.Invoke();
    }
}
