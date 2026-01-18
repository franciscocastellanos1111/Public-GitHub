
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
using System.Runtime.Remoting.Contexts;
using System.Runtime.Remoting.Services;

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



                AccountServicesHelper.writeToTrace("fetchExpressionQuery.Query: " + fetchExpressionQuery.Query
                                                                                                        , tracingService);



                XDocument fetchXmlDoc = XDocument.Parse(fetchExpressionQuery.Query);

                XElement entityElement = fetchXmlDoc.Descendants("entity").FirstOrDefault();
                string entityName = entityElement.Attributes("name").FirstOrDefault().Value;

                switch (entityName)
                {
                    case "incident":
                        incidentSearchQuery(fetchExpressionQuery
                                                        , context, service, tracingService);
                        return;

                    case "queueitem":
                        queueItemSearchQuery(fetchExpressionQuery
                                                        , context, service, tracingService);
                        return;

                }





            }
            catch (Exception e)
            {
                AccountServicesHelper.writeToTrace("Error during AccountServices.SearchQuery: " + e.Message
                                                                                                        , tracingService);
            }

        }
        public static void queueItemSearchQuery(FetchExpression fetchExpressionQuery
                                                                     , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService)
        {
            try
            {

                XDocument fetchXmlDoc = XDocument.Parse(fetchExpressionQuery.Query);


                XElement entityElement = fetchXmlDoc.Descendants("entity").FirstOrDefault();
                string entityName = entityElement.Attributes("name").FirstOrDefault().Value;

                bool includesMetaReference = entityElement.Descendants("attribute").ToList().Exists(element => element.Attribute("name") != null && element.Attribute("name").Value == "ts_tsaggregateordertotal");

                if (!includesMetaReference)
                    return;

                context.SharedVariables.Add("MetaReference", "queueitem.incident.salesorder");




                int linkEntities = entityElement.Descendants("link-entity").Count();
                AccountServicesHelper.writeToTrace("linkEntities: " + linkEntities.ToString()
                                                                                        , tracingService);
               


                XElement accountLinkEntityElement = entityElement.Descendants("link-entity").ToList().Find(element => element.Attribute("name") != null && element.Attribute("name").Value == "account");

                if (accountLinkEntityElement == null)
                    return;

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

                string accountNumberAlias = alias + ".accountnumber";

                if (!string.IsNullOrEmpty(accountNumberAlias))
                    context.SharedVariables.Add("AccountNumberAlias", accountNumberAlias);

                

                XElement incidentLinkEntityElement = entityElement.Descendants("link-entity").ToList().Find(element => element.Attribute("name") != null && element.Attribute("name").Value == "incident");

                if (incidentLinkEntityElement == null)
                    return;

                string incAlias = "";
                if (incidentLinkEntityElement.Attribute("alias") != null)
                    incAlias = incidentLinkEntityElement.Attribute("alias").Value;

                XElement tsaggregateordertotalAttribute = incidentLinkEntityElement.Descendants("attribute").ToList().Find(element => element.Attribute("name") != null && element.Attribute("name").Value == "ts_tsaggregateordertotal");

                if (tsaggregateordertotalAttribute == null)
                {
                    incidentLinkEntityElement.Add(
                                                new XElement("attribute",
                                                                new XAttribute("name", "ts_tsaggregateordertotal")
                                                                )
                                                );

                }

                string aggregateOrderTotalAlias = incAlias + ".ts_tsaggregateordertotal";

                if (!string.IsNullOrEmpty(aggregateOrderTotalAlias))
                    context.SharedVariables.Add("AggregateOrderTotalAlias", aggregateOrderTotalAlias);




                foreach (var sortItem in entityElement.Descendants("order"))
                {
                    string descending = sortItem.Attribute("descending") == null ? "null" : sortItem.Attribute("descending").Value;
                    AccountServicesHelper.writeToTrace("attribute: " + sortItem.Attribute("attribute").Value + "; descending: " + descending
                                                                                                                                    , tracingService);
                }

                foreach (var sortItem in incidentLinkEntityElement.Descendants("order"))
                {
                    string descending = sortItem.Attribute("descending") == null ? "null" : sortItem.Attribute("descending").Value;
                    AccountServicesHelper.writeToTrace("attribute: " + sortItem.Attribute("attribute").Value + "; descending: " + descending
                                                                                                                                , tracingService);
                }


                fetchXmlDoc.Descendants().FirstOrDefault().Attribute("count").Value = "5000";
                fetchExpressionQuery.Query = fetchXmlDoc.ToString();

                context.SharedVariables.Add("fetchXmlDocText", fetchXmlDoc.ToString());
                


                AccountServicesHelper.writeToTrace("fetchExpressionQuery.Query after catching salesorder: " + fetchExpressionQuery.Query
                                                                                                                                , tracingService);

            }
            catch (Exception e)
            {
                AccountServicesHelper.writeToTrace("Error in queueItemSearchQuery(...). Exception message: "
                                                                + Environment.NewLine + e.Message
                                                                , tracingService);
            }


        }
        public static void incidentSearchQuery(FetchExpression fetchExpressionQuery
                                                                     , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService)
        {
            try
            {


                XDocument fetchXmlDoc = XDocument.Parse(fetchExpressionQuery.Query);


                XElement entityElement = fetchXmlDoc.Descendants("entity").FirstOrDefault();
                string entityName = entityElement.Attributes("name").FirstOrDefault().Value;

                bool includesMetaReference = entityElement.Descendants("attribute").ToList().Exists(element => element.Attribute("name") != null && element.Attribute("name").Value == "ts_tsaggregateordertotal");

                if (!includesMetaReference)
                    return;

                context.SharedVariables.Add("MetaReference", "incident.salesorder");





                int linkEntities = entityElement.Descendants("link-entity").Count();
                AccountServicesHelper.writeToTrace("linkEntities: " + linkEntities.ToString()
                                                                                        , tracingService);






                XElement accountLinkEntityElement = entityElement.Descendants("link-entity").ToList().Find(element => element.Attribute("name") != null && element.Attribute("name").Value == "account");

                if (accountLinkEntityElement == null)
                    return;

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

                string accountNumberAlias = alias + ".accountnumber";

                if (!string.IsNullOrEmpty(accountNumberAlias))
                    context.SharedVariables.Add("AccountNumberAlias", accountNumberAlias);





                fetchXmlDoc.Descendants().FirstOrDefault().Attribute("count").Value = "5000";
                fetchExpressionQuery.Query = fetchXmlDoc.ToString();

                context.SharedVariables.Add("fetchXmlDocText", fetchXmlDoc.ToString());


                AccountServicesHelper.writeToTrace("fetchExpressionQuery.Query after catching salesorder: " + fetchExpressionQuery.Query
                                                                                                                                , tracingService);

            }
            catch (Exception e)
            {
                AccountServicesHelper.writeToTrace("Error in incidentSearchQuery(...). Exception message: "
                                                                + Environment.NewLine + e.Message
                                                                , tracingService);
            }

        }


        public static void accountSearchQuery(FetchExpression fetchExpressionQuery
                                                                      , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService)
        {}

    }
}