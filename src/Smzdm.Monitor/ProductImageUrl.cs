using System.Net;

namespace Smzdm.Monitor;

public static class ProductImageUrl
{
    public static bool TryNormalize(string? value, out string result)
    {
        result = "";
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 ||
            !Uri.TryCreate(new Uri("https://faxian.smzdm.com/"), WebUtility.HtmlDecode(value.Trim()), out var uri) ||
            uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0 || !uri.IsDefaultPort)
            return false;
        var host = uri.IdnHost;
        if (!(host.Equals("smzdm.com", StringComparison.OrdinalIgnoreCase) ||
              host.EndsWith(".smzdm.com", StringComparison.OrdinalIgnoreCase) ||
              host.Equals("zdmimg.com", StringComparison.OrdinalIgnoreCase) ||
              host.EndsWith(".zdmimg.com", StringComparison.OrdinalIgnoreCase)))
            return false;
        result = new UriBuilder(uri) { Scheme = "https", Port = -1, Fragment = "" }.Uri.AbsoluteUri;
        return true;
    }
}
