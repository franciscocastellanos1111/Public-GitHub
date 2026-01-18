
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
using System.Net.NetworkInformation;
using System.Collections;
using System.Security.Cryptography.Xml;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Messages;
using System.IdentityModel.Metadata;
using System.Web.Util;

namespace AccountServices
{
     
    public class RetrieveMultipleExternalData : IPlugin
    {
        static TimeZoneInfo pstZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        static Dictionary<string, string> EnvVariables;
        public void Execute(IServiceProvider serviceProvider)
        {
            
            ITracingService tracingService =
                (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            
            IPluginExecutionContext context = (IPluginExecutionContext)
                serviceProvider.GetService(typeof(IPluginExecutionContext));

            try
            {

                if (!context.SharedVariables.Contains("MetaReference"))
                    return;


                AccountServicesHelper.writeToTrace("Starting - AccountServices.RetrieveMultipleExternalData"
                                                                                                            , tracingService);


               string metaReference = (string)context.SharedVariables["MetaReference"];


                AccountServicesHelper.writeToTrace("metaReference: " + metaReference
                                                                                    , tracingService);



                if (!context.OutputParameters.Contains("BusinessEntityCollection"))
                    return;


                if (!(context.OutputParameters["BusinessEntityCollection"] is EntityCollection))
                    return;




                IOrganizationServiceFactory serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
                IOrganizationService service = serviceFactory.CreateOrganizationService(context.UserId);


                EnvVariables = AccountServicesHelper.GetEnvironmentVariables(service, tracingService);

                switch(metaReference)
                {
                    case "incident.salesorder":
                        processIncidentMetaReference(context, service, tracingService);
                        return;

                    case "queueitem.incident.salesorder":
                        processQueueItemtMetaReference(context, service, tracingService);
                        return;

                }

            }

            catch (Exception e)
            {
                AccountServicesHelper.writeToTrace("Error during AccountServices.RetrieveMultipleExternalData: " + e.Message
                                                                                                                        , tracingService);
            }

        }
        public static Entity addTotalAdmin(Entity resultEntity, string aggregateOrderTotalAlias, float admin)
        {
            Entity orgAdmin = resultEntity;
            orgAdmin[aggregateOrderTotalAlias] = new AliasedValue("incident", "ts_tsaggregateordertotal", admin);
            return orgAdmin;
        }

        public static Entity addTotalAdminIncident(Entity resultEntity, float admin)
        {
            Entity orgAdmin = resultEntity;
            orgAdmin["ts_tsaggregateordertotal"] = admin;
            return orgAdmin;

        }
        public static void processIncidentMetaReference(
                                                          IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService)
        {
            try
            {
                EntityCollection queryResult = (EntityCollection)context.OutputParameters["BusinessEntityCollection"];


                string tsOrgIdAlias = (string)context.SharedVariables["AccountNumberAlias"];

                var tsOrgIds = queryResult.Entities.Select(item => (string)item.GetAttributeValue<AliasedValue>(tsOrgIdAlias).Value).ToArray();

                string tsOrgIdsCsv = string.Join(",", tsOrgIds);


                AccountServicesHelper.writeToTrace("Result count: " + queryResult.TotalRecordCount.ToString()
                                                                            + "\n" + "tsOrgIds count: " + tsOrgIds.Length.ToString()
                                                                            + "\n" + "queryResult.Entities.ToList().Count(): " + queryResult.Entities.ToList().Count()
                                                                            , tracingService);




                Dictionary<string, float> orgTotalAdmin = getOrgsOrderData(tsOrgIdsCsv
                                                                                    , service, tracingService);


                AccountServicesHelper.writeToTrace("First org - tsOrgId: " + orgTotalAdmin.First().Key + "; AdminTotal: " + orgTotalAdmin.First().Value.ToString()
                                                                                                                                                                , tracingService);


                var orgsAdminList = queryResult.Entities.ToList().Select(item =>
                                                                            addTotalAdminIncident(item, orgTotalAdmin[(string)item.GetAttributeValue<AliasedValue>(tsOrgIdAlias).Value])
                                                                            ).ToList();



                Entity highest = orgsAdminList.OrderByDescending(item => item.GetAttributeValue<float>("totalAdmin")).First();

                AccountServicesHelper.writeToTrace("highest - tsOrgId: " + (string)highest.GetAttributeValue<AliasedValue>(tsOrgIdAlias).Value + "; AdminTotal: " + highest.GetAttributeValue<float>("totalAdmin").ToString()
                                                                                                                                                                                                                        , tracingService);


                var orgsAdminListOrder = orgsAdminList.OrderByDescending(item => item.GetAttributeValue<float>("ts_tsaggregateordertotal")).ToList();

                EntityCollection orgsAdminCollection = new EntityCollection(orgsAdminListOrder);

                orgsAdminCollection.EntityName = queryResult.EntityName;

                context.OutputParameters["BusinessEntityCollection"] = orgsAdminCollection;
            }
            catch (Exception e)
            {
                AccountServicesHelper.writeToTrace("Error in processIncidentMetaReference(...): " + e.Message
                                                                                                        , tracingService);
            }
        }


        public static void processQueueItemtMetaReference(
                                                          IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService)
        {

            try
            {

                EntityCollection queryResult = (EntityCollection)context.OutputParameters["BusinessEntityCollection"];


                string tsOrgIdAlias = (string)context.SharedVariables["AccountNumberAlias"];
                string aggregateOrderTotalAlias = (string)context.SharedVariables["AggregateOrderTotalAlias"];

                string[] aliasPrefix = aggregateOrderTotalAlias.Split('.');

                var tsOrgIds = queryResult.Entities.Select(item => (string)item.GetAttributeValue<AliasedValue>(tsOrgIdAlias).Value).ToArray();

                string tsOrgIdsCsv = string.Join(",", tsOrgIds);


                AccountServicesHelper.writeToTrace("Result count: " + queryResult.TotalRecordCount.ToString()
                                                                                        + "\n" + "tsOrgIds count: " + tsOrgIds.Length.ToString()
                                                                                        + "\n" + "queryResult.Entities.ToList().Count(): " + queryResult.Entities.ToList().Count()
                                                                                        , tracingService);


               

                Dictionary<string, float> orgTotalAdmin = getOrgsOrderData(tsOrgIdsCsv
                                                                                    , service, tracingService);


                AccountServicesHelper.writeToTrace("First org - tsOrgId: " + orgTotalAdmin.First().Key + "; AdminTotal: " + orgTotalAdmin.First().Value.ToString()
                                                                                                                                                                , tracingService);


                var orgsAdminList = queryResult.Entities.ToList().Select(item =>
                                                                            addTotalAdmin(item, aggregateOrderTotalAlias, orgTotalAdmin[(string)item.GetAttributeValue<AliasedValue>(tsOrgIdAlias).Value])
                                                                            ).ToList();


               

                string fetchXmlDocText = (string)context.SharedVariables["fetchXmlDocText"];

                XDocument fetchXmlDoc = XDocument.Parse(fetchXmlDocText);

                XElement entityElement = fetchXmlDoc.Descendants("entity").FirstOrDefault();
                string entityName = entityElement.Attributes("name").FirstOrDefault().Value;



                XElement incidentLinkEntityElement = entityElement.Descendants("link-entity").ToList().Find(element => element.Attribute("name") != null && element.Attribute("name").Value == "incident");


                //var sortedList = orgsAdminList.OrderByDescending(item => item.GetAttributeValue<float>("ts_tsaggregateordertotal")).ToList();

                //orgsAdminList.OrderBy(element => element.GetAttributeValue<float>(sortItem.Attribute("attribute").Value));

                //IOrderedEnumerable <Entity>  =
                //                sortedList =  ;

                var sortedList = orgsAdminList.OrderByDescending(item =>
                                                                                (float)
                                                                                    (item.GetAttributeValue<AliasedValue>(aggregateOrderTotalAlias).Value)
                                                                                );
                int count = 0;
                foreach (var sortItem in entityElement.Elements("order"))
                {
                    count++;
                    string descending = sortItem.Attribute("descending") == null ? "null" : sortItem.Attribute("descending").Value;

                    AccountServicesHelper.writeToTrace("order attribute on queue item: " + sortItem.Attribute("attribute").Value + "; descending: " + descending
                                                                                                                                        , tracingService);



                    


                    Entity firstRec = orgsAdminList.First();
                    object attribute = firstRec[sortItem.Attribute("attribute").Value];
                    //var nreT = GetGeneric(attribute);
                    string typeName = attribute.GetType().Name;


                    AccountServicesHelper.writeToTrace("order attribute on queue item typeName: " + typeName
                                                                                                                                       , tracingService);
                    if (descending == "true")
                    {

                        switch (typeName)
                        {
                            case "DateTime":
                                if (count == 1)
                                {
                                    sortedList = orgsAdminList.OrderByDescending(element => element.GetAttributeValue<DateTime>(sortItem.Attribute("attribute").Value));
                                }
                                else
                                {
                                    sortedList = sortedList.ThenByDescending(element => element.GetAttributeValue<DateTime>(sortItem.Attribute("attribute").Value));
                                }
                                break;
                            case "float":
                                if (count == 1)
                                {
                                    sortedList = orgsAdminList.OrderByDescending(element => element.GetAttributeValue<float>(sortItem.Attribute("attribute").Value));
                                }
                                else
                                {
                                    sortedList = sortedList.ThenByDescending(element => element.GetAttributeValue<float>(sortItem.Attribute("attribute").Value));
                                }
                                break;
                            case "string":
                                if (count == 1)
                                {
                                    sortedList = orgsAdminList.OrderByDescending(element => element.GetAttributeValue<string>(sortItem.Attribute("attribute").Value));
                                }
                                else
                                {
                                    sortedList = sortedList.ThenByDescending(element => element.GetAttributeValue<string>(sortItem.Attribute("attribute").Value));
                                }
                                break;
                            case "int":
                            case "int32":
                                if (count == 1)
                                {
                                    sortedList = orgsAdminList.OrderByDescending(element => element.GetAttributeValue<int>(sortItem.Attribute("attribute").Value));
                                }
                                else
                                {
                                    sortedList = sortedList.ThenByDescending(element => element.GetAttributeValue<int>(sortItem.Attribute("attribute").Value));
                                }

                                break;
                        }

                    }
                    else
                    {
                        switch (typeName)
                        {
                            case "DateTime":
                                if (count == 1)
                                {
                                    sortedList = orgsAdminList.OrderBy(element => element.GetAttributeValue<DateTime>(sortItem.Attribute("attribute").Value));
                                }
                                else
                                {
                                    sortedList = sortedList.ThenBy(element => element.GetAttributeValue<DateTime>(sortItem.Attribute("attribute").Value));
                                }
                                break;
                            case "float":
                                if (count == 1)
                                {
                                    sortedList = orgsAdminList.OrderBy(element => element.GetAttributeValue<float>(sortItem.Attribute("attribute").Value));
                                }
                                else
                                {
                                    sortedList = sortedList.ThenBy(element => element.GetAttributeValue<float>(sortItem.Attribute("attribute").Value));
                                }
                                break;
                            case "string":
                                if (count == 1)
                                {
                                    sortedList = orgsAdminList.OrderBy(element => element.GetAttributeValue<string>(sortItem.Attribute("attribute").Value));
                                }
                                else
                                {
                                    sortedList = sortedList.ThenBy(element => element.GetAttributeValue<string>(sortItem.Attribute("attribute").Value));
                                }
                                break;
                            case "int":
                            case "int32":
                                if (count == 1)
                                {
                                    sortedList = orgsAdminList.OrderBy(element => element.GetAttributeValue<int>(sortItem.Attribute("attribute").Value));
                                }
                                else
                                {
                                    sortedList = sortedList.ThenBy(element => element.GetAttributeValue<int>(sortItem.Attribute("attribute").Value));
                                }

                                break;
                        }
                    }


                    if (descending == "true")
                        sortedList.Reverse();
                }



                foreach (var sortItem in incidentLinkEntityElement.Elements("order"))
                {

                    count++;
                    string descending = sortItem.Attribute("descending") == null ? "null" : sortItem.Attribute("descending").Value;

                    AccountServicesHelper.writeToTrace("order attribute on incident: " + sortItem.Attribute("attribute").Value + "; descending: " + descending
                                                                                                                                        , tracingService);



                    AccountServicesHelper.writeToTrace("aliasPrefix[0].sortItem.Attribute(\"attribute\").Value: " + aliasPrefix[0] + "." + sortItem.Attribute("attribute").Value
                                                                                                                                       , tracingService);



                    AccountServicesHelper.writeToTrace("aggregateOrderTotalAlias: " + aggregateOrderTotalAlias
                                                                                                          , tracingService);

                    Entity firstRec = orgsAdminList.First();
                    object attribute = firstRec[aliasPrefix[0] + "." + sortItem.Attribute("attribute").Value];
                    string typeName = attribute.GetType().Name;


                    AccountServicesHelper.writeToTrace("order attribute on incident typeName: " + typeName
                                                                                                     , tracingService);
                    if (descending == "true")
                    {
                        switch (typeName)
                        {
                            
                            case "AliasedValue":
                                if (aggregateOrderTotalAlias == aliasPrefix[0] + "." + sortItem.Attribute("attribute").Value)
                                {
                                    if (count == 1)
                                    {
                                        sortedList = orgsAdminList.OrderByDescending(item =>
                                                                                        (float)
                                                                                            (item.GetAttributeValue<AliasedValue>(aliasPrefix[0] + "." + sortItem.Attribute("attribute").Value).Value)
                                                                                        );
                                    }
                                    else
                                    {
                                        sortedList = sortedList.ThenByDescending(item =>
                                                                                         (float)
                                                                                             (item.GetAttributeValue<AliasedValue>(aliasPrefix[0] + "." + sortItem.Attribute("attribute").Value).Value)
                                                                                         );
                                    }
                                }
                                else if (sortItem.Attribute("attribute").Value.EndsWith("on"))
                                {
                                    if (count == 1)
                                    {
                                        sortedList = orgsAdminList.OrderByDescending(item =>
                                                                                        (DateTime)
                                                                                            (item.GetAttributeValue<AliasedValue>(aliasPrefix[0] + "." + sortItem.Attribute("attribute").Value).Value)
                                                                                        );
                                    }
                                    else
                                    {
                                        sortedList = sortedList.ThenByDescending(item =>
                                                                                         (DateTime)
                                                                                             (item.GetAttributeValue<AliasedValue>(aliasPrefix[0] + "." + sortItem.Attribute("attribute").Value).Value)
                                                                                         );
                                    }
                                }
                                else
                                {
                                    if (count == 1)
                                    {
                                        sortedList = orgsAdminList.OrderByDescending(item =>

                                                                                            (item.GetAttributeValue<AliasedValue>(aliasPrefix[0] + "." + sortItem.Attribute("attribute").Value).Value).ToString()
                                                                                        );
                                    }
                                    else
                                    {
                                        sortedList = sortedList.ThenByDescending(item =>

                                                                                            (item.GetAttributeValue<AliasedValue>(aliasPrefix[0] + "." + sortItem.Attribute("attribute").Value).Value).ToString()
                                                                                        );
                                    }
                                }
                                    break;
                        }
                    }
                    else
                    {

                        switch (typeName)
                        {
                           

                            case "AliasedValue":
                                if (aggregateOrderTotalAlias == aliasPrefix[0] + "." + sortItem.Attribute("attribute").Value)
                                {
                                    if (count == 1)
                                    {
                                        sortedList = orgsAdminList.OrderBy(item =>
                                                                                        (float)
                                                                                            (item.GetAttributeValue<AliasedValue>(aliasPrefix[0] + "." + sortItem.Attribute("attribute").Value).Value)
                                                                                        );
                                    }
                                    else
                                    {
                                        sortedList = sortedList.ThenBy(item =>
                                                                                         (float)
                                                                                             (item.GetAttributeValue<AliasedValue>(aliasPrefix[0] + "." + sortItem.Attribute("attribute").Value).Value)
                                                                                         );
                                    }
                                }
                                else if (sortItem.Attribute("attribute").Value.EndsWith("on"))
                                {
                                    if (count == 1)
                                    {
                                        sortedList = orgsAdminList.OrderByDescending(item =>
                                                                                        (DateTime)
                                                                                            (item.GetAttributeValue<AliasedValue>(aliasPrefix[0] + "." + sortItem.Attribute("attribute").Value).Value)
                                                                                        );
                                    }
                                    else
                                    {
                                        sortedList = sortedList.ThenByDescending(item =>
                                                                                         (DateTime)
                                                                                             (item.GetAttributeValue<AliasedValue>(aliasPrefix[0] + "." + sortItem.Attribute("attribute").Value).Value)
                                                                                         );
                                    }
                                }
                                else
                                {
                                    if (count == 1)
                                    {
                                        sortedList = orgsAdminList.OrderBy(item =>

                                                                                            (item.GetAttributeValue<AliasedValue>(aliasPrefix[0] + "." + sortItem.Attribute("attribute").Value).Value).ToString()
                                                                                        );
                                    }
                                    else
                                    {
                                        sortedList = sortedList.ThenBy(item =>

                                                                                            (item.GetAttributeValue<AliasedValue>(aliasPrefix[0] + "." + sortItem.Attribute("attribute").Value).Value).ToString()
                                                                                        );
                                    }
                                }
                                break;
                        }
                    }



                }


                EntityCollection orgsAdminCollection = new EntityCollection(sortedList.ToList());
                //orgsAdminListOrder);

                orgsAdminCollection.EntityName = queryResult.EntityName;

                context.OutputParameters["BusinessEntityCollection"] = orgsAdminCollection;

            }
            catch (Exception e)
            {
                AccountServicesHelper.writeToTrace("Error in processQueueItemtMetaReference(...): " + e.Message
                                                                                                        , tracingService);
            }
        }


        public static T GetGeneric<T>(T obj)
        {
            return (T)Activator.CreateInstance(typeof(T));


            //Type typeParameterType = typeof(T);
            //string typeName = typeParameterType.Name;

            //switch (typeName)
            //{
            //    case "SendEmailRequest":
            //        SendEmailRequest entity = obj as SendEmailRequest;
            //        string subje = entity.EmailId.ToString();
            //        break;
            //    case "RetrieveEntityRequest":
            //        RetrieveEntityRequest request = obj as RetrieveEntityRequest;
            //        string name = request.LogicalName;
            //        break;



            //}

        }
    
/*
         case "DateTime":
                                if (count == 1)
                                {

                                    sortedList = orgsAdminList.OrderBy(item =>
                                                                                    (DateTime)
                                                                                        (item.GetAttributeValue<AliasedValue>(aliasPrefix[0] + "." + sortItem.Attribute("attribute").Value).Value)
                                                                                    );
                                }
                                else
                                {
                                    sortedList = sortedList.ThenBy(item =>
                                                                                     (DateTime)
                                                                                         (item.GetAttributeValue<AliasedValue>(aliasPrefix[0] + "." + sortItem.Attribute("attribute").Value).Value)
                                                                                     );
                                }
break;
                            case "float":

    AccountServicesHelper.writeToTrace("case float aggregateOrderTotalAlias: " + aggregateOrderTotalAlias
        , tracingService);


    AccountServicesHelper.writeToTrace(aggregateOrderTotalAlias + "     " + aliasPrefix[0] + "." + sortItem.Attribute("attribute").Value
                                                                                                                                                            , tracingService);


    if (count == 1)//& aggregateOrderTotalAlias != aliasPrefix[0] + "." + sortItem.Attribute("attribute").Value && )
    {
        sortedList = orgsAdminList.OrderBy(item =>
                                                        (float)
                                                            (item.GetAttributeValue<AliasedValue>(aliasPrefix[0] + "." + sortItem.Attribute("attribute").Value).Value)
                                                        );
    }
    else
    {
        sortedList = sortedList.ThenBy(item =>
                                                         (float)
                                                             (item.GetAttributeValue<AliasedValue>(aliasPrefix[0] + "." + sortItem.Attribute("attribute").Value).Value)
                                                         );
    }
    break;
case "string":
    if (count == 1)
    {
        sortedList = orgsAdminList.OrderBy(item =>
                                                        (string)
                                                            (item.GetAttributeValue<AliasedValue>(aliasPrefix[0] + "." + sortItem.Attribute("attribute").Value).Value)
                                                        );
    }
    else
    {
        sortedList = sortedList.ThenBy(item =>
                                                        (string)
                                                            (item.GetAttributeValue<AliasedValue>(aliasPrefix[0] + "." + sortItem.Attribute("attribute").Value).Value)
                                                         );
    }
    break;
case "int":
case "int32":
    if (count == 1)
    {
        sortedList = orgsAdminList.OrderBy(item =>
                                                        (int)
                                                            (item.GetAttributeValue<AliasedValue>(aliasPrefix[0] + "." + sortItem.Attribute("attribute").Value).Value)
                                                        );
    }
    else
    {
        sortedList = sortedList.ThenBy(item =>
                                                        (int)
                                                            (item.GetAttributeValue<AliasedValue>(aliasPrefix[0] + "." + sortItem.Attribute("attribute").Value).Value)
                                                        );
    }
    break;
*/






        //Entity firstRec = queryResult.Entities.First();
        //if (
        //        !(
        //        context.SharedVariables.Contains("AccountNumberAlias") && firstRec.Contains((string)context.SharedVariables["AccountNumberAlias"])
        //        )
        //    )
        //{ return; }

    //string tsOrgIdAlias = (string)context.SharedVariables["AccountNumberAlias"];





    /*

    string tsOrgIds = String.Join(",", tsOrgIdList.ToArray());

    AccountServicesHelper.writeToTrace("Result count: " + queryResult.TotalRecordCount.ToString()
                                            + Environment.NewLine + "tsOrgIds: " + tsOrgIds
                                            + Environment.NewLine + "queryResult.Entities.ToList().Count(): " + queryResult.Entities.ToList().Count()
                                            , tracingService);


    Dictionary<string, bool> orgsWithOrders = getOrgsAndOrdersFlag(tsOrgIds
                                                                    , service, tracingService);

    if (orgsWithOrders == null)
        return;
    */


    //var queryResultWithOrders = queryResult.Entities.ToList().Where(item => orgsWithOrders[item.GetAttributeValue<AliasedValue>(tsOrgIdAlias).Value.ToString()]);

    //EntityCollection orgsWithOrdersCollection = new EntityCollection(queryResultWithOrders.ToList());

    //orgsWithOrdersCollection.EntityName = queryResult.EntityName;






    //context.OutputParameters["BusinessEntityCollection"] = orgsWithOrdersCollection;



        public static Dictionary<string, bool> getOrgsAndOrdersFlag(string tsOrgIds
                                                                                , IOrganizationService service, ITracingService tracingService)
        {
            Dictionary<string, bool> orgsWithOrders = null;
            try
            {

                X509Certificate2 cer = AccountServicesHelper.GetVaultCertificate(EnvVariables, tracingService);

                var binding = new BasicHttpsBinding();
                binding.Security.Mode = BasicHttpsSecurityMode.Transport;
                binding.Security.Transport.ClientCredentialType = HttpClientCredentialType.Certificate;
                binding.MaxReceivedMessageSize = 52428800;
                binding.MaxBufferSize = 52428800;
                binding.MaxBufferPoolSize = 52428800;

                DataAccessServiceClient dataAccessClient = new DataAccessServiceClient(binding, new EndpointAddress(EnvVariables["ts_ESBUrl"] + @"services/TSGDataAccessServiceEBS_V1"));
                dataAccessClient.ClientCredentials.ClientCertificate.Certificate = cer;

                ExecuteStoredProcRequestType objRequest = new ExecuteStoredProcRequestType();


                objRequest.ServerName = EnvVariables["ts_Sql2k14Server"];
                objRequest.DBName = "ServiceAdmin";
                objRequest.SPName = "usp_orgsWithOrders";

                objRequest.@params = new inputParamsType();
                XmlDocument doc = new XmlDocument();
                List<XmlElement> elements = new List<XmlElement>();

                XmlElement param1 = doc.CreateElement("tsOrgIds");
                param1.InnerText = tsOrgIds;
                elements.Add(param1);


                objRequest.@params.Any = elements.ToArray();


                ExecuteStoredProcResponseType dataAccessresponse = dataAccessClient.ExecuteStoredProc(objRequest);

                rowType[] returnXml = dataAccessresponse.ReturnXml;

                orgsWithOrders = new Dictionary<string, bool>();

                foreach (rowType row in returnXml)
                {
                    orgsWithOrders.Add(row.Any[0].InnerText, bool.Parse(row.Any[1].InnerText));

                }


            }
            catch (Exception e)
            {
                string error = "Error in getOrgsAndOrdersFlag(...). Exception message: "
                                    + Environment.NewLine + e.Message;
                
                AccountServicesHelper.writeToTrace(error 
                                                            , tracingService);
            }

            return orgsWithOrders;
        }

        public static Dictionary<string, float> getOrgsOrderData(string tsOrgIds
                                                                                , IOrganizationService service, ITracingService tracingService)
        {
            Dictionary<string, float> orgsWithOrders = null;
            try
            {

                X509Certificate2 cer = AccountServicesHelper.GetVaultCertificate(EnvVariables, tracingService);

                var binding = new BasicHttpsBinding();
                binding.Security.Mode = BasicHttpsSecurityMode.Transport;
                binding.Security.Transport.ClientCredentialType = HttpClientCredentialType.Certificate;
                binding.MaxReceivedMessageSize = 52428800;
                binding.MaxBufferSize = 52428800;
                binding.MaxBufferPoolSize = 52428800;

                DataAccessServiceClient dataAccessClient = new DataAccessServiceClient(binding, new EndpointAddress(EnvVariables["ts_ESBUrl"] + @"services/TSGDataAccessServiceEBS_V1"));
                dataAccessClient.ClientCredentials.ClientCertificate.Certificate = cer;

                ExecuteStoredProcRequestType objRequest = new ExecuteStoredProcRequestType();


                objRequest.ServerName = EnvVariables["ts_Sql2k14Server"];
                objRequest.DBName = "ServiceAdmin";
                objRequest.SPName = "usp_orgsOrderData";

                objRequest.@params = new inputParamsType();
                XmlDocument doc = new XmlDocument();
                List<XmlElement> elements = new List<XmlElement>();

                XmlElement param1 = doc.CreateElement("tsOrgIds");
                param1.InnerText = tsOrgIds;
                elements.Add(param1);


                objRequest.@params.Any = elements.ToArray();


                ExecuteStoredProcResponseType dataAccessResponse = dataAccessClient.ExecuteStoredProc(objRequest);

                rowType[] returnXml = dataAccessResponse.ReturnXml;

                orgsWithOrders = new Dictionary<string, float>();

                foreach (rowType row in returnXml)
                {
                    orgsWithOrders.Add(row.Any[0].InnerText, float.Parse(row.Any[1].InnerText));

                }


            }
            catch (Exception e)
            {
                string error = "Error in getOrgsOrderData(...). Exception message: "
                                                + Environment.NewLine + e.Message;

                AccountServicesHelper.writeToTrace(error
                                                                , tracingService);
            }

            return orgsWithOrders;
        }

    }
}