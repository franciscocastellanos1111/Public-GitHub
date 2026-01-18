# Simple test using our Network Validation Service
Write-Host "=== Strategic Website Validation Test ===" -ForegroundColor Green
Write-Host "Testing 5 strategic websites from your list..." -ForegroundColor Yellow
Write-Host ""

# Test domains (cleaned)
$testDomains = @(
    "danse-saint-andre.fr",      # First one (French)
    "instagram.com",              # Highly reputable (US)  
    "elthamcemetery.com",         # Last one (Australian)
    "brooklyn.cuny.edu",          # US Educational
    "mogu27.ru"                   # Russian domain
)

foreach ($domain in $testDomains) {
    Write-Host " Testing: $domain" -ForegroundColor Cyan
    Write-Host "---------------------------------------------------"
    
    try {
        # DNS Lookup
        Write-Host " DNS Resolution:" -ForegroundColor White
        $dnsResult = Resolve-DnsName -Name $domain -Type A -ErrorAction SilentlyContinue
        if ($dnsResult) {
            $ip = $dnsResult[0].IPAddress
            Write-Host "    Resolves to: $ip" -ForegroundColor Green
            
            # Basic WHOIS lookup (simplified)
            Write-Host " Domain Analysis:" -ForegroundColor White
            
            # Determine likely country from TLD
            $tld = $domain.Split('.')[1]
            $country = switch ($tld) {
                "fr" { "France (FR)" }
                "com" { "Commercial (likely US)" }
                "edu" { "US Educational" }
                "ru" { "Russia (RU)" }
                default { "Unknown" }
            }
            Write-Host "    TLD Analysis: $country" -ForegroundColor Yellow
            
            # Check if TLD suggests US registration
            $isUSLikely = $tld -in @("com", "edu", "org", "net", "us", "gov", "mil")
            Write-Host "    Likely US: $isUSLikely" -ForegroundColor ($isUSLikely ? "Green" : "Red")
            
            # IP Geolocation attempt (basic)
            Write-Host " IP Analysis:" -ForegroundColor White
            Write-Host "    IP Address: $ip" -ForegroundColor Gray
            
            # Check if IP is in common US ranges (very basic)
            $ipBytes = $ip.Split('.') | ForEach-Object { [int]$_ }
            $isPrivate = ($ipBytes[0] -eq 10) -or ($ipBytes[0] -eq 172 -and $ipBytes[1] -ge 16 -and $ipBytes[1] -le 31) -or ($ipBytes[0] -eq 192 -and $ipBytes[1] -eq 168)
            
            if ($isPrivate) {
                Write-Host "    Private IP Address" -ForegroundColor Yellow
            } else {
                Write-Host "    Public IP Address" -ForegroundColor Green
            }
            
        } else {
            Write-Host "    DNS Resolution Failed" -ForegroundColor Red
        }
        
    } catch {
        Write-Host "    Error: $($_.Exception.Message)" -ForegroundColor Red
    }
    
    Write-Host ""
    Start-Sleep -Seconds 2
}

Write-Host "=== Test Complete ===" -ForegroundColor Green
Write-Host "This gives us a basic validation. For full analysis, we need to run the complete NetworkValidationService." -ForegroundColor Yellow
