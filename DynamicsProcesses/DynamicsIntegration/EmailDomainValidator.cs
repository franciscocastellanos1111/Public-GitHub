using System;
using System.Collections.Generic;
using System.Linq;

namespace DynamicsProcesses
{
    public static class EmailDomainValidator
    {
        // Common third-party email providers that should be skipped for domain validation
        private static readonly HashSet<string> CommonEmailProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Major providers
            "gmail.com", "googlemail.com", "yahoo.com", "yahoo.co.uk", "yahoo.ca", "yahoo.com.au",
            "hotmail.com", "hotmail.co.uk", "hotmail.ca", "hotmail.com.au", "hotmail.de", "hotmail.fr",
            "outlook.com", "outlook.co.uk", "outlook.ca", "outlook.com.au", "outlook.de", "outlook.fr",
            "live.com", "live.co.uk", "live.ca", "live.com.au", "live.de", "live.fr",
            "msn.com", "aol.com", "aol.co.uk", "aol.ca", "aol.com.au",
            
            // Apple
            "icloud.com", "me.com", "mac.com",
            
            // Other major providers
            "protonmail.com", "proton.me", "tutanota.com", "mail.com", "gmx.com", "gmx.de",
            "yandex.com", "yandex.ru", "mail.ru", "rambler.ru",
            
            // Corporate/Professional
            "zoho.com", "zohomail.com", "fastmail.com", "hey.com",
            
            // Regional providers
            "btinternet.com", "sky.com", "virginmedia.com", "talktalk.net", // UK
            "rogers.com", "bell.net", "sympatico.ca", // Canada
            "bigpond.com", "optusnet.com.au", "telstra.com", // Australia
            "t-online.de", "web.de", "freenet.de", // Germany
            "orange.fr", "wanadoo.fr", "free.fr", "laposte.net", // France
            "tiscali.it", "libero.it", "alice.it", // Italy
            "terra.com.br", "uol.com.br", "globo.com", "bol.com.br", // Brazil
            
            // Temporary/Disposable email providers (should also be flagged as suspicious)
            "10minutemail.com", "tempmail.org", "guerrillamail.com", "mailinator.com",
            "throwaway.email", "temp-mail.org", "getnada.com"
        };

        // Disposable email providers that should be flagged as high risk
        private static readonly HashSet<string> DisposableEmailProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "10minutemail.com", "10minutemail.net", "tempmail.org", "temp-mail.org", "temp-mail.io",
            "guerrillamail.com", "guerrillamail.net", "guerrillamail.org", "guerrillamail.de",
            "mailinator.com", "mailinator.net", "throwaway.email", "getnada.com", "sharklasers.com",
            "trashmail.com", "yopmail.com", "maildrop.cc", "mailnesia.com", "tempail.com",
            "dispostable.com", "fakemailgenerator.com", "emailondeck.com", "mytrashmail.com"
        };

        /// <summary>
        /// Determines if an email domain should be validated for fraud detection
        /// </summary>
        /// <param name="emailDomain">The domain part of the email address</param>
        /// <returns>True if the domain should be validated, False if it's a common provider</returns>
        public static bool ShouldValidateDomain(string emailDomain)
        {
            if (string.IsNullOrEmpty(emailDomain))
                return false;

            return !CommonEmailProviders.Contains(emailDomain.ToLower());
        }

        /// <summary>
        /// Checks if the email domain is a disposable/temporary email provider
        /// </summary>
        /// <param name="emailDomain">The domain part of the email address</param>
        /// <returns>True if it's a disposable email provider</returns>
        public static bool IsDisposableEmailProvider(string emailDomain)
        {
            if (string.IsNullOrEmpty(emailDomain))
                return false;

            return DisposableEmailProviders.Contains(emailDomain.ToLower());
        }

       
        public static EmailAnalysisResult AnalyzeEmail(string email)
        {
            var result = new EmailAnalysisResult { Email = email };

            if (string.IsNullOrEmpty(email) || !email.Contains("@"))
            {
                result.IsValid = false;
                result.Issues.Add("Invalid email format");
                return result;
            }

            var parts = email.Split('@');
            if (parts.Length != 2)
            {
                result.IsValid = false;
                result.Issues.Add("Invalid email format");
                return result;
            }

            result.LocalPart = parts[0];
            result.Domain = parts[1].ToLower();
            result.IsValid = true;

            // Check for disposable email
            if (IsDisposableEmailProvider(result.Domain))
            {
                result.IsDisposable = true;
                result.RiskScore += 50;
                result.Issues.Add($"Uses disposable email provider: {result.Domain}");
            }

            // Check for suspicious patterns in local part
            //if (HasSuspiciousLocalPart(result.LocalPart))
            //{
            //    result.RiskScore += 20;
            //    result.Issues.Add("Email local part shows suspicious patterns");
            //}

            // Check if domain should be validated
            result.ShouldValidateDomain = ShouldValidateDomain(result.Domain);

            // Calculate risk level
            if (result.RiskScore >= 40)
                result.RiskLevel = "HIGH";
            else if (result.RiskScore >= 20)
                result.RiskLevel = "MEDIUM";
            else if (result.RiskScore >= 10)
                result.RiskLevel = "LOW";
            else
                result.RiskLevel = "MINIMAL";

            return result;
        }

        private static bool HasSuspiciousLocalPart(string localPart)
        {
            if (string.IsNullOrEmpty(localPart))
                return false;

            // Check for excessive numbers or random-looking patterns
            var numberCount = localPart.Count(char.IsDigit);
            var letterCount = localPart.Count(char.IsLetter);
            
            // More than 70% numbers could indicate generated/fake email
            if (letterCount > 0 && (double)numberCount / localPart.Length > 0.7)
                return true;

            // Very long local parts (>20 chars) can be suspicious
            if (localPart.Length > 20)
                return true;

            // Common patterns in fake emails
            var suspiciousPatterns = new[] { "test", "temp", "fake", "spam", "noreply", "donotreply" };
            if (suspiciousPatterns.Any(pattern => localPart.ToLower().Contains(pattern)))
                return true;

            return false;
        }
    }

    public class EmailAnalysisResult
    {
        public string Email { get; set; }
        public string LocalPart { get; set; }
        public string Domain { get; set; }
        public bool IsValid { get; set; } = true;
        public bool IsDisposable { get; set; }
        public bool ShouldValidateDomain { get; set; }
        public int RiskScore { get; set; }
        public string RiskLevel { get; set; } = "MINIMAL";
        public List<string> Issues { get; set; } = new List<string>();

        public bool HasFraudIndicators => RiskScore >= 20;
        public bool IsHighRisk => RiskLevel == "HIGH";
    }
}