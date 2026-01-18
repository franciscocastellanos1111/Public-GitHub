using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Configuration;
using System.Diagnostics;
using System.Data.SqlClient;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Sdk;
using System.Xml.Linq;
using Microsoft.Identity.Client;
using System.Runtime.Remoting.Services;
using Newtonsoft.Json;

namespace DynamicsProcesses
{
    internal class DynamicsIntegrationProcesses
    {

        public static Dictionary<string, string> DynamicsEnvironments = new Dictionary<string, string>()
        {
           { "dev" , "https://org90a61c80.crm.dynamics.com" },
            { "qa", "https://tsdynamicsqa.crm.dynamics.com"},
            { "stage" , "https://tsdynamicsstage.crm.dynamics.com" },
            { "prod" , "https://techsoup.crm.dynamics.com" },
            { "https://org90a61c80.crm.dynamics.com" , "dev" },
            { "https://tsdynamicsqa.crm.dynamics.com", "qa"},
            {  "https://tsdynamicsstage.crm.dynamics.com" , "stage"},
            { "https://techsoup.crm.dynamics.com" , "prod" }

        };

        public static void getCtpOrgIdsForAccounts()
        {
            try
            {
                if (DynamicsEnvironments.ContainsKey(DynamicsInterface.DynamicsEnvironment))
                {
                    string DynamicsEnvironmentCurrentName = DynamicsEnvironments[DynamicsInterface.DynamicsEnvironment];
                    DynamicsEnvironments["DynamicsEnvironmentCurrent"] = DynamicsEnvironmentCurrentName;
                }



                int hoursOld = 0;
                string hoursOldText = DynamicsInterface.Args.Length > 1 ? DynamicsInterface.Args[1] : "";
                if (int.TryParse(hoursOldText, out hoursOld)) { }

                DateTime dateRef = DateTime.UtcNow.AddHours(-1 * hoursOld);



                QueryExpression queryAccount = new QueryExpression("account");
                queryAccount.ColumnSet = new ColumnSet("accountid", "accountnumber", "ts_ctporgid");
                queryAccount.Criteria.AddCondition("ts_ctporgid", ConditionOperator.Null);
                queryAccount.Criteria.AddCondition("createdon", ConditionOperator.GreaterEqual, dateRef);
                queryAccount.AddOrder("createdon", OrderType.Descending);
                EntityCollection accountCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(queryAccount);

                foreach (Entity account in accountCollection.Entities)
                {
                    updateCtpOrgIdOnAccount(account);
                }
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in getCtpOrgIdsForAccounts(). Exception message: " + Environment.NewLine + e.Message);
            }
        }

        public static void updateCtpOrgIdOnAccount(Entity account)
        {
            try
            {
                string tsOrgId = account.GetAttributeValue<string>("accountnumber");

                string ctpValue = getCtpValueFromApi(tsOrgId);

                if (string.IsNullOrEmpty(ctpValue))
                {
                    if (DynamicsEnvironments["DynamicsEnvironmentCurrent"] != "prod")
                    {
                        ctpValue = account.GetAttributeValue<Guid>("accountid").ToString();
                    }
                    else
                    {
                        return;
                    }  
                }
                account["ts_ctporgid"] = ctpValue;
                DynamicsInterface.DataverseClient.Update(account);

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in updateCtpOrgIdOnAccount(). Exception message: " + Environment.NewLine + e.Message);
            }
        }
        private static string getCtpValueFromApi(string tsOrgId)
        {
            try
            {
                string requestUrl = $"https://msftphase3.tsgctp.org/services/xmap/v_001/ffa07369-c658-41c5-8322-4cf76f2142ea/?id={tsOrgId}";

                HttpClient client = new HttpClient();

                HttpResponseMessage response = client.GetAsync(
                                                                requestUrl
                                                                ).Result;

                
                if (!response.IsSuccessStatusCode)
                {
                    DynamicsInterface.writeToLog($"getCtpValueFromApi call failed with status code: {response.StatusCode}");
                    return null;
                }


                string responseTxt = response.Content.ReadAsStringAsync().Result;

                DynamicsInterface.writeToLog($"getCtpValueFromApi response for tsOrgId {tsOrgId}: {responseTxt}");


                dynamic responseObj = JsonConvert.DeserializeObject(responseTxt);

                if (responseObj?.returnStatus?.data?.results == null || responseObj.returnStatus.data.results.Count == 0)
                    return null;

                var firstResult = responseObj.returnStatus.data.results[0];

                string ctpValue = firstResult?.data?.ctp?.ToString();

                client.Dispose();


                return ctpValue;               

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in getCtpValueFromApi(). Exception message: " + Environment.NewLine + e.Message
                                                + Environment.NewLine + "tsOrgId: " + tsOrgId
                                                );
            }

            return null;
        }

    }
}







