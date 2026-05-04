using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.ServiceModel;
using System.Text;
using System.Configuration;
using System.Security.Cryptography;
using TechSoup.Services.Configuration;

namespace TechSoup.Services.NetSuiteTokenService
{
    
    public class NetSuiteTokenService : INetSuiteTokenService
    {
        public SOAPTokenResponseType GetSOAPToken(SOAPTokenRequestType request)
        {
            OAuthBase authBase = new OAuthBase();
            string timeStamp = authBase.GenerateTimeStamp();
            string nonce = authBase.GenerateNonce();


            string baseString = request.accountId + "&" + request.consumerKey + "&" + request.tokenId + "&" + nonce + "&" + timeStamp;


            IdSecretPairsSection section = ConfigurationManager.GetSection("IdSecretPairs") as IdSecretPairsSection;
            string consumerSecret = section.getSecret("consumerkey", request.consumerKey);
            if (string.IsNullOrEmpty(consumerSecret))
            {
                return new SOAPTokenResponseType()
                {
                    error = "Consumer Key was not found"
                };
            }

            string tokenSecret = section.getSecret("token", request.tokenId);
            if (string.IsNullOrEmpty(tokenSecret))
            {
                return new SOAPTokenResponseType()
                {
                    error = "Token Id was not found"
                };
            }

            string key = consumerSecret + "&" + tokenSecret;


            string signature = "";

            var encoding = new System.Text.ASCIIEncoding();

            byte[] keyByte = encoding.GetBytes(key);

            byte[] messageBytes = encoding.GetBytes(baseString);


            //using (var myhmacsha1 = new HMACSHA1(keyByte))
            //{
            //    byte[] hashmessage = myhmacsha1.ComputeHash(messageBytes);
            //    signature = Convert.ToBase64String(hashmessage);

            //}


            using (var myhmacsha256 = new HMACSHA256(keyByte))
            {
                byte[] hashmessage = myhmacsha256.ComputeHash(messageBytes);
                signature = Convert.ToBase64String(hashmessage);

            }

            SOAPTokenResponseType response = new SOAPTokenResponseType();
            response.nonce = nonce;
            response.timeStamp = timeStamp;
            response.signature = signature;


            return response;
            
        }

        public RESTTokenResponseType GetRESTSToken(RESTTokenRequestType request)
        {
            OAuthBase authBase = new OAuthBase();
            string timeStamp = authBase.GenerateTimeStamp();
            string nonce = authBase.GenerateNonce();
            Uri url = new Uri(request.url);

            string norm = "";
            string norm1 = "";



            IdSecretPairsSection section = ConfigurationManager.GetSection("IdSecretPairs") as IdSecretPairsSection;
            string consumerSecret = section.getSecret("consumerkey", request.consumerKey);
            if (string.IsNullOrEmpty(consumerSecret))
            {
                return new RESTTokenResponseType()
                {
                    error = "Consumer Key was not found"
                };
            }

            string tokenSecret = section.getSecret("token", request.tokenId);
            if (string.IsNullOrEmpty(tokenSecret))
            {
                return new RESTTokenResponseType()
                {
                    error = "Token Id was not found"
                };
            }

            string httpMethod = request.httpMethod;
            if (string.IsNullOrEmpty(httpMethod))
            {
                httpMethod = "GET";
            }

            //string signature = authBase.GenerateSignature(url, request.consumerKey, consumerSecret, request.tokenId, tokenSecret, httpMethod, timeStamp, nonce, out norm, out norm1);

            string signature = authBase.GenerateSignature256(url, request.consumerKey, consumerSecret, request.tokenId, tokenSecret, httpMethod, timeStamp, nonce, out norm, out norm1);

            if (signature.Contains("+"))
            {
                signature = signature.Replace("+", "%2B");
            }

            //string header = "Authorization: OAuth ";
            //header += "oauth_signature=\"" + signature + "\",";
            //header += "oauth_version=\"1.0\",";
            //header += "oauth_nonce=\"" + nonce + "\",";
            //header += "oauth_signature_method=\"HMAC-SHA1\",";
            //header += "oauth_consumer_key=\"" + request.consumerKey + "\",";
            //header += "oauth_token=\"" + request.tokenId + "\",";
            //header += "oauth_timestamp=\"" + timeStamp + "\",";
            //header += "realm=\"" + request.accountId + "\"";

            string header = "Authorization: OAuth ";
            header += "oauth_signature=\"" + signature + "\",";
            header += "oauth_version=\"1.0\",";
            header += "oauth_nonce=\"" + nonce + "\",";
            header += "oauth_signature_method=\"HMAC-SHA256\",";
            header += "oauth_consumer_key=\"" + request.consumerKey + "\",";
            header += "oauth_token=\"" + request.tokenId + "\",";
            header += "oauth_timestamp=\"" + timeStamp + "\",";
            header += "realm=\"" + request.accountId + "\"";


            return new RESTTokenResponseType()
            {
                authorization = header.Substring(15)
            };

        }
    }

  
}
