
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.ServiceModel;


using Microsoft.Identity.Client;
using System.Net.Http;
using System.Net.Http.Headers;


using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

using System.Security.Cryptography.X509Certificates;
using AccountServices.DataAccessService;
using System.Xml;
using System.Collections.Generic;
using System.Security.Principal;
using AccountServices.orderService;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Text;
using System.Security.Cryptography;

namespace AccountServices
{
     
    public class SearchQuery : IPlugin
    {
        static TimeZoneInfo pstZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");

        public void Execute(IServiceProvider serviceProvider)
        {
            
            ITracingService tracingService =
                (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            
            IPluginExecutionContext context = (IPluginExecutionContext)
                serviceProvider.GetService(typeof(IPluginExecutionContext));            




            try
            {
                IOrganizationServiceFactory serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
                IOrganizationService service = serviceFactory.CreateOrganizationService(context.UserId);


                var thisQuery = context.InputParameters["Query"];
                var fetchExpressionQuery = thisQuery as FetchExpression;

                if (fetchExpressionQuery == null)
                    return;

                var ine = context.InputParameters.Select(x => x.Key + " | " + x.Value).ToArray();

                var paranmcsv = string.Join(";", ine);
                AccountServicesHelper.writeToTrace("inputparameters: " + paranmcsv
                                                        , tracingService);


                AccountServicesHelper.writeToTrace("fetchExpressionQuery.Query: " + fetchExpressionQuery.Query
                                                        , tracingService);



                XDocument fetchXmlDoc = XDocument.Parse(fetchExpressionQuery.Query);


                XElement entityElement = fetchXmlDoc.Descendants("entity").FirstOrDefault();
                string entityName = entityElement.Attributes("name").FirstOrDefault().Value;


                bool hasOrderReference = entityElement.Descendants("link-entity").ToList().Exists(element => element.Attribute("name") != null && element.Attribute("name").Value == "salesorder");


                if (!hasOrderReference)
                    return;


                int linkEntities = entityElement.Descendants("link-entity").Count();


                AccountServicesHelper.writeToTrace("linkEntities: " + linkEntities.ToString()
                                                        , tracingService);

                context.SharedVariables.Add("IncludesMetaCriteria", true);

                entityElement.Descendants("link-entity").Where(element => element.Attribute("name") != null && element.Attribute("name").Value == "salesorder")
                                                                    .ToList().ForEach(element => { element.Remove();}
                                                                    );


                fetchXmlDoc.Descendants().FirstOrDefault().Attribute("count").Value = "5000";

                string accountNumberAlias = "";

                if (entityName == "incident")
                {
                    XElement accountLinkEntityElement = entityElement.Descendants("link-entity").Where(element => element.Attribute("name") != null && element.Attribute("name").Value == "account").First();

                    string alias = "";
                    if (accountLinkEntityElement.Attribute("alias") != null)
                        alias = accountLinkEntityElement.Attribute("alias").Value;

                    XElement tsOrgIdAttribute = accountLinkEntityElement.Descendants("attribute").ToList().Find(element => element.Attribute("name") != null && element.Attribute("name").Value == "accountnumber");

                    if (tsOrgIdAttribute == null)
                    {
                        accountLinkEntityElement.Add(
                                                    new XElement("attribute",
                                                                    new XAttribute("name", "accountnumber")
                                                                    )
                                                    );

                    }
                    accountNumberAlias = alias + ".accountnumber";

                }

                fetchExpressionQuery.Query = fetchXmlDoc.ToString();


                if (!string.IsNullOrEmpty(accountNumberAlias))
                    context.SharedVariables.Add("AccountNumberAlias", accountNumberAlias);
                


                AccountServicesHelper.writeToTrace("fetchExpressionQuery.Query after catching salesorder: " + fetchExpressionQuery.Query
                                                        , tracingService);





            }
            catch (Exception e)
            {
                AccountServicesHelper.writeToTrace("Error during AccountServices.SearchQuery: " + e.Message
                                                        , tracingService);
            }

        }


    }
}