# Seed the server with 15 feeds for stress testing
# Usage: Run after starting the server and before load tests
# Requires: Server running on https://localhost:9443 with test auth

param(
    [string]$BaseUrl = "https://localhost:9443"
)

$feeds = @(
    "https://feeds.arstechnica.com/arstechnica/index",
    "https://www.theverge.com/rss/index.xml",
    "https://hnrss.org/frontpage",
    "https://feeds.bbci.co.uk/news/technology/rss.xml",
    "https://rss.nytimes.com/services/xml/rss/nyt/Technology.xml",
    "https://www.wired.com/feed/rss",
    "https://techcrunch.com/feed/",
    "https://feeds.feedburner.com/TheHackersNews",
    "https://www.schneier.com/feed/atom/",
    "https://blog.cloudflare.com/rss/",
    "https://github.blog/feed/",
    "https://devblogs.microsoft.com/dotnet/feed/",
    "https://stackoverflow.blog/feed/",
    "https://martinfowler.com/feed.atom",
    "https://xkcd.com/rss.xml"
)

Write-Host "=== Seeding stress test data ==="

# Register user
Write-Host "Registering user..."
$r = Invoke-WebRequest -Uri "$BaseUrl/api/user/register" -Method POST -SkipCertificateCheck -TimeoutSec 10 -SkipHttpErrorCheck
Write-Host "  Register: $($r.StatusCode)"

# Add feeds
Write-Host "Adding $($feeds.Count) feeds..."
$added = 0
foreach ($url in $feeds) {
    $body = "{`"href`":`"$url`",`"userId`":1}"
    $r = Invoke-WebRequest -Uri "$BaseUrl/api/feed" -Method POST -Body $body -ContentType "application/json" -SkipCertificateCheck -TimeoutSec 10 -SkipHttpErrorCheck
    if ($r.StatusCode -eq 201) { $added++ }
    $shortUrl = $url.Substring(0, [Math]::Min(50, $url.Length))
    Write-Host "  $($r.StatusCode): $shortUrl..."
}
Write-Host "Added $added / $($feeds.Count) feeds"

# Trigger refresh
Write-Host "Triggering refresh..."
Invoke-WebRequest -Uri "$BaseUrl/api/feed/refresh" -SkipCertificateCheck -TimeoutSec 10 -SkipHttpErrorCheck | Out-Null

# Wait for items to populate
Write-Host "Waiting 45s for feed refresh to complete..."
Start-Sleep -Seconds 45

# Check results
$r = Invoke-WebRequest -Uri "$BaseUrl/api/feed" -SkipCertificateCheck -TimeoutSec 10 -SkipHttpErrorCheck
$feedList = $r.Content | ConvertFrom-Json
Write-Host "Feeds in DB: $($feedList.Count)"

$r = Invoke-WebRequest -Uri "$BaseUrl/api/item/timeline?page=0&pageSize=20" -SkipCertificateCheck -TimeoutSec 10 -SkipHttpErrorCheck
$items = $r.Content | ConvertFrom-Json
Write-Host "Timeline items (page 0): $($items.Count)"

# Try page 1 to confirm depth
$r = Invoke-WebRequest -Uri "$BaseUrl/api/item/timeline?page=1&pageSize=20" -SkipCertificateCheck -TimeoutSec 10 -SkipHttpErrorCheck
$items2 = $r.Content | ConvertFrom-Json
Write-Host "Timeline items (page 1): $($items2.Count)"

# Total rough estimate
$total = 0
for ($p = 0; $p -lt 20; $p++) {
    $r = Invoke-WebRequest -Uri "$BaseUrl/api/item/timeline?page=$p&pageSize=50" -SkipCertificateCheck -TimeoutSec 10 -SkipHttpErrorCheck
    $page = $r.Content | ConvertFrom-Json
    if ($page.Count -eq 0) { break }
    $total += $page.Count
}
Write-Host "`n=== Seed complete: $($feedList.Count) feeds, ~$total items ==="
