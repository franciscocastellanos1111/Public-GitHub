# Quick Batch Processor for Website Validation
Write-Host "=== Full Website Validation Batch ===" -ForegroundColor Green
Write-Host "Processing all 114 websites from SampleWebsites.json..." -ForegroundColor Yellow
Write-Host ""

# Load the JSON file
$jsonContent = Get-Content "SampleWebsites.json" -Raw
$websites = $jsonContent | ConvertFrom-Json

Write-Host "Loaded $($websites.Count) websites for validation" -ForegroundColor Cyan
Write-Host ""

# Initialize counters
$validDomains = 0
$usDomains = 0
$nonUSdomains = 0
$failedDomains = 0
$results = @()

# Process each website
for ($i = 0; $i -lt $websites.Count; $i++) {
    $website = $websites[$i]
    $progress = $i + 1
    
    Write-Host "[$progress/$($websites.Count)] Processing: $website" -ForegroundColor White
    
    try {
        # Clean the domain
        $cleanDomain = $website -replace "https://", "" -replace "http://", "" -replace "www\.", ""
        $cleanDomain = ($cleanDomain -split "/")[0]
        $cleanDomain = ($cleanDomain -split "\?")[0]
        $cleanDomain = ($cleanDomain -split "#")[0]
        $cleanDomain = $cleanDomain.Trim().ToLower()
        
        # DNS Resolution
        $dnsResult = Resolve-DnsName -Name $cleanDomain -Type A -ErrorAction SilentlyContinue
        
        if ($dnsResult) {
            $validDomains++
            $ip = $dnsResult[0].IPAddress
            
            # Basic TLD analysis
            $tld = ($cleanDomain -split "\\.")[-1]
            $isUSLikely = $tld -in @("com", "edu", "org", "net", "us", "gov", "mil")
            
            if ($isUSLikely) {
                $usDomains++
                $usStatus = "Likely US"
                $statusColor = "Green"
            } else {
                $nonUSdomains++
                $usStatus = "Non-US ($tld)"
                $statusColor = "Yellow"
            }
            
            Write-Host "   Valid | IP: $ip | $usStatus" -ForegroundColor $statusColor
            
            # Store result
            $results += [PSCustomObject]@{
                OriginalURL = $website
                CleanDomain = $cleanDomain
                IP = $ip
                TLD = $tld
                IsValid = $true
                IsUSLikely = $isUSLikely
                Status = $usStatus
            }
        } else {
            $failedDomains++
            Write-Host "   DNS Resolution Failed" -ForegroundColor Red
            
            $results += [PSCustomObject]@{
                OriginalURL = $website
                CleanDomain = $cleanDomain
                IP = "N/A"
                TLD = ($cleanDomain -split "\\.")[-1]
                IsValid = $false
                IsUSLikely = $false
                Status = "Failed"
            }
        }
        
    } catch {
        $failedDomains++
        Write-Host "   Error: $($_.Exception.Message)" -ForegroundColor Red
    }
    
    # Brief pause to be respectful
    Start-Sleep -Milliseconds 500
}

Write-Host ""
Write-Host "=== VALIDATION SUMMARY ===" -ForegroundColor Green
Write-Host "Total Websites: $($websites.Count)" -ForegroundColor White
Write-Host "Valid Domains: $validDomains ($([math]::Round(($validDomains/$websites.Count)*100, 1))%)" -ForegroundColor Green
Write-Host "Likely US Domains: $usDomains ($([math]::Round(($usDomains/$websites.Count)*100, 1))%)" -ForegroundColor Cyan
Write-Host "Non-US Domains: $nonUSdomains ($([math]::Round(($nonUSdomains/$websites.Count)*100, 1))%)" -ForegroundColor Yellow
Write-Host "Failed Domains: $failedDomains ($([math]::Round(($failedDomains/$websites.Count)*100, 1))%)" -ForegroundColor Red
Write-Host ""

# Generate CSV report
$results | Export-Csv -Path "WebsiteValidationResults.csv" -NoTypeInformation
Write-Host "Results exported to: WebsiteValidationResults.csv" -ForegroundColor Green

# Show top countries by TLD
Write-Host ""
Write-Host "=== TOP COUNTRIES BY TLD ===" -ForegroundColor Green
$tldStats = $results | Where-Object { $_.IsValid -eq $true } | Group-Object TLD | Sort-Object Count -Descending | Select-Object -First 10
foreach ($tld in $tldStats) {
    $countryName = switch ($tld.Name) {
        "com" { "Commercial (Global/US)" }
        "org" { "Organization (Global/US)" }
        "net" { "Network (Global/US)" }
        "edu" { "US Education" }
        "gov" { "US Government" }
        "us" { "United States" }
        "ca" { "Canada" }
        "uk" { "United Kingdom" }
        "au" { "Australia" }
        "de" { "Germany" }
        "fr" { "France" }
        "it" { "Italy" }
        "nl" { "Netherlands" }
        "ru" { "Russia" }
        "ch" { "Switzerland" }
        "be" { "Belgium" }
        "ie" { "Ireland" }
        "nz" { "New Zealand" }
        "se" { "Sweden" }
        "es" { "Spain" }
        "br" { "Brazil" }
        "ng" { "Nigeria" }
        "co" { "Colombia" }
        "cl" { "Chile" }
        "tw" { "Taiwan" }
        "nu" { "Niue" }
        "info" { "Information (Global)" }
        "ngo" { "Non-Governmental Organization" }
        default { "Unknown ($($tld.Name))" }
    }
    Write-Host "  $($tld.Name): $($tld.Count) domains - $countryName" -ForegroundColor White
}

Write-Host ""
Write-Host "=== VALIDATION COMPLETE ===" -ForegroundColor Green
