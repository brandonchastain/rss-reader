namespace RssReader.Shared.Extensions
{
    public static class StringExtensions
    {
        public static string GetRootDomain(this string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return string.Empty;
            }

            string rootDomain = new Uri(url).Authority.ToLowerInvariant();

            string prefix = "www.";
            if (rootDomain.StartsWith(prefix))
            {
                rootDomain = rootDomain.Substring(prefix.Length);
            }

            var parts = rootDomain.Split('.');
            if (parts.Length > 2)
            {
                rootDomain = string.Join('.', parts.Skip(parts.Length - 2));
            }

            return rootDomain;
        }
    }
}