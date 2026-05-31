namespace BrowserService.Stealth;

/// <summary>
/// Navigator 环境信息
/// </summary>
public class NavigatorInfo
{
    private static readonly Random _random = new();
    private static readonly string[] UserAgents =
    [
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36",
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36",
    ];

    private static readonly string[] Vendors = ["Google Inc.", ""];

    private static readonly string[] WebGLVendors =
    [
        "Google Inc. (Intel)",
        "Google Inc. (NVIDIA)",
        "Google Inc. (AMD)",
        "Intel Inc.",
    ];

    private static readonly string[] WebGLRenderers =
    [
        "ANGLE (Intel, Intel(R) UHD Graphics 620 (0x00005917) Direct3D11 vs_5_0 ps_5_0, D3D11)",
        "ANGLE (NVIDIA, NVIDIA GeForce RTX 3060 Direct3D11 vs_5_0 ps_5_0, D3D11)",
        "ANGLE (AMD, AMD Radeon(TM) Graphics Direct3D11 vs_5_0 ps_5_0, D3D11)",
        "Intel Iris OpenGL Engine",
        "ANGLE (Intel, Intel(R) UHD Graphics 630 (0x00003E9B) Direct3D11 vs_5_0 ps_5_0, D3D11)",
    ];

    private static readonly int[] MemorySizes = [4, 8, 8, 8, 8, 8, 8, 8];

    public string UserAgent { get; } = UserAgents[_random.Next(UserAgents.Length)];
    public string Vendor { get; } = Vendors[_random.Next(Vendors.Length)];
    public string WebGLVendor { get; } = WebGLVendors[_random.Next(WebGLVendors.Length)];
    public string WebGLRenderer { get; } = WebGLRenderers[_random.Next(WebGLRenderers.Length)];
    public int MemorySize { get; } = MemorySizes[_random.Next(MemorySizes.Length)];
}