using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.ServiceModel;
using System.Text;
using System.Configuration;
using System.Security.Cryptography;
using Microsoft.Xrm.Sdk;

namespace EDServices
{
    
    public class NetSuiteTokenGenerator
    {
        public static SOAPTokenResponseType GetSOAPToken(SOAPTokenRequestType request, ITracingService tracingService, List<string> errorStack)
        {
            try
            {
                OAuthBase authBase = new OAuthBase();
                string timeStamp = authBase.GenerateTimeStamp();
                string nonce = authBase.GenerateNonce(tracingService, errorStack);


                string baseString = request.accountId + "&" + EDServicesHelper.EnvVariables["ts_NetSuiteConsumerKey"] + "&" + EDServicesHelper.EnvVariables["ts_NetSuiteTokenId"] + "&" + nonce + "&" + timeStamp;


                string consumerSecret = EDServicesHelper.EnvVariables["ts_NetSuiteConsumerSecret"];

                string tokenSecret = EDServicesHelper.EnvVariables["ts_NetSuiteTokenSecret"];

                string key = consumerSecret + "&" + tokenSecret;


                string signature = "";

                var encoding = new System.Text.ASCIIEncoding();

                byte[] keyByte = encoding.GetBytes(key);

                byte[] messageBytes = encoding.GetBytes(baseString);


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
            catch (Exception e)
            {
                string error = "Error: " + e.Message;
                EDServicesHelper.writeToTrace("At GetSOAPToken(SOAPTokenRequestType request). " + error, tracingService
                    );
                errorStack.Add(error);
                return null;
            }

        }

        public static RESTTokenResponseType GetRESTSToken(RESTTokenRequestType request, ITracingService tracingService, List<string> errorStack)
        {
            try
            {
                OAuthBase authBase = new OAuthBase();
                string timeStamp = authBase.GenerateTimeStamp();
                string nonce = authBase.GenerateNonce(tracingService, errorStack);
                Uri url = new Uri(request.url);

                string norm = "";
                string norm1 = "";


                string consumerSecret = EDServicesHelper.EnvVariables["ts_NetSuiteConsumerSecret"];

                string tokenSecret = EDServicesHelper.EnvVariables["ts_NetSuiteTokenSecret"];

                string httpMethod = request.httpMethod;
                if (string.IsNullOrEmpty(httpMethod))
                {
                    httpMethod = "GET";
                }


                string signature = authBase.GenerateSignature256(url, request.consumerKey, consumerSecret, request.tokenId, tokenSecret, httpMethod, timeStamp, nonce, out norm, out norm1);

                if (signature.Contains("+"))
                {
                    signature = signature.Replace("+", "%2B");
                }

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
            catch (Exception e)
            {
                string error = "Error: " + e.Message;
                EDServicesHelper.writeToTrace("At GetRESTSToken(RESTTokenRequestType request). " + error, tracingService);
                errorStack.Add(error);
                return null;
            }
        }
    }

    [DataContract]
    public class SOAPTokenRequestType
    {
        [DataMember(Order = 0)]
        public string accountId { get; set; }

        [DataMember(Order = 1)]
        public string consumerKey { get; set; }

        [DataMember(Order = 2)]
        public string tokenId { get; set; }
    }

    [DataContract]
    public class RESTTokenRequestType
    {
        [DataMember(Order = 0)]
        public string accountId { get; set; }

        [DataMember(Order = 1)]
        public string consumerKey { get; set; }

        [DataMember(Order = 2)]
        public string tokenId { get; set; }

        [DataMember(Order = 3)]
        public string url { get; set; }

        [DataMember(Order = 4)]
        public string httpMethod { get; set; }
    }



    [DataContract]
    public class SOAPTokenResponseType
    {
        [DataMember(Order = 0, EmitDefaultValue = false)]
        public string nonce { get; set; }

        [DataMember(Order = 1, EmitDefaultValue = false)]
        public string timeStamp { get; set; }

        [DataMember(Order = 2, EmitDefaultValue = false)]
        public string signature { get; set; }

        [DataMember(Order = 3, EmitDefaultValue = false)]
        public string error { get; set; }
    }


    [DataContract]
    public class RESTTokenResponseType
    {
        [DataMember(Order = 0, EmitDefaultValue = false)]
        public string authorization { get; set; }

        [DataMember(Order = 3, EmitDefaultValue = false)]
        public string error { get; set; }
    }
}
