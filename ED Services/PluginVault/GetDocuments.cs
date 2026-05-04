
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.ServiceModel;


//using Microsoft.Identity.Client;
using System.Net.Http;
using System.Net.Http.Headers;



using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System.Threading;
using System.Collections.Generic;
using System.Collections;
using static System.Net.WebRequestMethods;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Messages;

//using System.Security.Cryptography.X509Certificates;
//using PluginVault.DataAccessService;
//using System.Xml;

namespace EDServices
{
     
    public class GetDocuments : IPlugin
    {
        static Dictionary<string, string> EnvVariables;
        static TimeZoneInfo pstZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        public void Execute(IServiceProvider serviceProvider)
        {

            
            ITracingService tracingService =
                (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            
            IPluginExecutionContext context = (IPluginExecutionContext)
                serviceProvider.GetService(typeof(IPluginExecutionContext));

            
            tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "Starting - EDServices.GetDocuments");       
            tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));

            
            try
            {


                /**********GET Custom API Request Parameters*********/
                int edIdInt = (int)context.InputParameters["ts_edid"];
                /****************************************************/

                string edId = edIdInt.ToString();

                tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "Edid: " + edId);
                tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));

                /*******************************************************************/

                IOrganizationServiceFactory serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
                IOrganizationService service = serviceFactory.CreateOrganizationService(null);
                
                
                
                RetrieveCurrentOrganizationRequest request = new RetrieveCurrentOrganizationRequest();
                RetrieveCurrentOrganizationResponse response = (RetrieveCurrentOrganizationResponse)service.Execute(request);


                tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "envURL: " + response.Detail.UrlName);
                //tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "envId: " + response.Detail.EnvironmentId);
                tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));



                QueryExpression queryExpIncident = new QueryExpression("incident");
                queryExpIncident.ColumnSet.AddColumns("incidentid");
                FilterExpression filter = new FilterExpression(LogicalOperator.And);
                filter.AddCondition("ts_edid", ConditionOperator.Equal, edId);
                queryExpIncident.Criteria.AddFilter(filter);
                EntityCollection incidentCollection = service.RetrieveMultiple(queryExpIncident);

                //Entity incident = incidentCollection.Entities.First();
                //Guid incidentId = incident.GetAttributeValue<Guid>("incidentid");
                //string incidentIdString = incidentId.ToString();

                Guid incidentId = incidentCollection.Entities.First().Id;

                QueryExpression queryExpIncidentAttach = new QueryExpression("msdyn_entityattachment");
                queryExpIncidentAttach.ColumnSet.AddColumns("msdyn_entityattachmentid", "msdyn_name", "ts_category", "msdyn_fileblob", "modifiedon");
                FilterExpression filterIncAttach = new FilterExpression(LogicalOperator.And);
                filterIncAttach.AddCondition("msdyn_relatedentity", ConditionOperator.Equal, incidentId);
                queryExpIncidentAttach.Criteria.AddFilter(filterIncAttach);
                EntityCollection incidentAttachCollection = service.RetrieveMultiple(queryExpIncidentAttach);



                EntityCollection edDocsCol = new EntityCollection();

                foreach (Entity incidentAttach in incidentAttachCollection.Entities)
                {
                    string docName = incidentAttach.GetAttributeValue<string>("msdyn_name");
                    //string docCategory = incidentAttach.GetAttributeValue<int>("ts_category").f

                    DateTime docModifiedOn = incidentAttach.GetAttributeValue<DateTime>("modifiedon");
                    string docCategory = string.Empty;
                    int docCategoryCode = 0;
                    if (incidentAttach.Contains("ts_category"))
                    {
                        docCategory = incidentAttach.FormattedValues["ts_category"];
                        docCategoryCode = incidentAttach.GetAttributeValue<OptionSetValue>("ts_category").Value;

                        
                    }


                    Entity attachmentRec =  RetrievehFileInfo(service, incidentAttach.Id, tracingService);

                    string docURL = $"https://{response.Detail.UrlName}.crm.dynamics.com/api/data/v9.2/msdyn_entityattachments({incidentAttach.Id.ToString()})/msdyn_fileblob/$value";
                    Entity edDoc = new Entity()
                    {
                        Attributes =
                        {
                            { "documentName", docName}
                            ,{"documentId", incidentAttach.Id.ToString()}
                            ,{ "documentCategoryCode", docCategoryCode }
                            ,{ "documentCategory", docCategory }
                            ,{ "documentType", attachmentRec["mimetype"]}
                            ,{ "documentSizeInBytes", attachmentRec["filesizeinbytes"]}
                            ,{ "documentUpdateDate", TimeZoneInfo.ConvertTimeFromUtc(docModifiedOn, pstZone).ToString("yyyy-MM-dd HH:mm:ss")}
                            ,{ "documentURL", docURL }
                            
                        }
                    };


                    edDocsCol.Entities.Add(edDoc);


                    
                    //Guid attachmentId = incidentAttach.GetAttributeValue<Guid>("msdyn_fileblob");
                    //Entity attachmentRec = service.Retrieve("fileattachment", attachmentId, 
                    //    new ColumnSet(
                    //                        "createdon",
                    //                        "mimetype",
                    //                        "filesizeinbytes",
                    //                        "filename")
                    //    );


                    //tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "retrieved file info: ");
                    //tracingService.Trace("file type: " + attachmentRec.GetAttributeValue<string>("mimetype"));
                    //tracingService.Trace("filesizeinbytes: " + attachmentRec.GetAttributeValue<string>("filesizeinbytes"));
                    //tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));


                }





                /**********ASSIGN Custom API Response Parameters*********/
                context.OutputParameters["ts_documents"] = edDocsCol;
                context.OutputParameters["ts_edid"] = Int64.Parse(edId);
                /********************************************************/



                



            }

            catch (Exception e)
            {

                tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "Error during EDServices.GetDocuments: " + e.Message);
                tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));
            }

        }


        static Entity RetrievehFileInfo(IOrganizationService service,Guid incidentAttachId, ITracingService tracingService)
        {

            try
            {
                // Create query for related records
                var relationshipQueryCollection = new RelationshipQueryCollection
                {
                    {
                        new Relationship("msdyn_entityattachment_FileAttachments"),
                        new QueryExpression("fileattachment")
                        {
                            ColumnSet = new ColumnSet(
                                            "createdon",
                                            "mimetype",
                                            "filesizeinbytes",
                                            "filename",
                                            "regardingfieldname",
                                            "fileattachmentid")
                        }
                    }
                };



                // Include the related query with the Retrieve Request
                RetrieveRequest request = new RetrieveRequest
                {
                    ColumnSet = new ColumnSet("msdyn_entityattachmentid"),
                    RelatedEntitiesQuery = relationshipQueryCollection,
                    Target = new EntityReference("msdyn_entityattachment", incidentAttachId)
                };

                // Send the request
                RetrieveResponse response = (RetrieveResponse)service.Execute(request);

                //Display related FileAttachment data for the account record
                response.Entity.RelatedEntities[new Relationship("msdyn_entityattachment_FileAttachments")]
                    .Entities.ToList().ForEach(e =>
                    {

                        tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "File attachment");
                        tracingService.Trace($"createdon: {e.FormattedValues["createdon"]}");
                        tracingService.Trace($"createdon unformatted: {e["createdon"]}");
                        tracingService.Trace($"mimetype: {e["mimetype"]}");
                        tracingService.Trace($"filesizeinbytes: {e.FormattedValues["filesizeinbytes"]}");
                        tracingService.Trace($"filesizeinbytes unformatted: {e["filesizeinbytes"]}");
                        tracingService.Trace($"filename: {e["filename"]}");
                        tracingService.Trace($"regardingfieldname: {e["regardingfieldname"]}");
                        tracingService.Trace($"fileattachmentid: {e["fileattachmentid"]}");

                        tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));





                    });

                return response.Entity.RelatedEntities[new Relationship("msdyn_entityattachment_FileAttachments")]
                    .Entities.First();

            }
            catch (Exception e)
            {

                tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "Error during RetrievehFileInfo: " + e.Message);
                tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));
            }


            return null;
        }




    }
}