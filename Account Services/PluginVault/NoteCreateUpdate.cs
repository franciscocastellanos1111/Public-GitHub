
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
using System.Dynamic;
using System.Security;

namespace AccountServices
{
     
    public class NoteCreateUpdate : IPlugin
    {
        static TimeZoneInfo pstZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        static Dictionary<string, string> EnvVariables;

        public void Execute(IServiceProvider serviceProvider)
        {
            
            ITracingService tracingService =
                (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            
            IPluginExecutionContext context = (IPluginExecutionContext)
                serviceProvider.GetService(typeof(IPluginExecutionContext));


            AccountServicesHelper.writeToTrace("Starting - AccountServices.NoteCreateUpdate", tracingService);


            try
            {
                IOrganizationServiceFactory serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
                IOrganizationService service = serviceFactory.CreateOrganizationService(null);

                EnvVariables = AccountServicesHelper.GetEnvironmentVariables(service, tracingService);



                Entity annotationTargetEntity = (Entity)context.InputParameters["Target"];

                if (annotationTargetEntity.LogicalName != "annotation")
                    return;

                Entity annotation = service.Retrieve("annotation", annotationTargetEntity.Id, new ColumnSet(true));


                AccountServicesHelper.writeToTrace("annotation.Id: " + annotation.Id.ToString(), tracingService);


                processNoteCreate(annotation
                                            , context, service, tracingService);

                if (
                    context.MessageName.Equals("Update", StringComparison.OrdinalIgnoreCase)
                      || context.MessageName.Equals("Create", StringComparison.OrdinalIgnoreCase)
                      )
                    validateSystemNoteOwner(annotation
                                                        , context, service, tracingService);


            }

            catch (Exception e)
            {
                AccountServicesHelper.writeToTrace($"Error during AccountServices.NoteCreateUpdate. Exception message:{Environment.NewLine}{e.Message}"
                                                                                                                                                , tracingService);


                
            }

        }

        public static void processNoteCreate(Entity annotation
                                                                , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService)
        {
            try
            {

                if (!context.MessageName.Equals("Create", StringComparison.OrdinalIgnoreCase))
                    return;


                AccountServicesHelper.writeToTrace($"processNoteCreate() - Start{Environment.NewLine}noteId: {annotation.Id}"
                                                                                                                            , tracingService);

                Entity user = service.Retrieve("systemuser", context.InitiatingUserId, new ColumnSet(true));
                string fullName = user.GetAttributeValue<string>("fullname");


                AccountServicesHelper.writeToTrace($"processNoteCreate(). InitiatingUserId: {fullName}"
                                                                                            , tracingService);

                if (
                    fullName.Contains("TSDynamics") || fullName.Contains("TSDynamicsOnyx")
                    //|| fullName.Contains("TSDynamicsOnyx") || fullName.Contains("DynamicsClient") || fullName.Contains("DynamicsESBIntegration") || fullName.Contains("SYSTEM")
                    )
                {
                    AccountServicesHelper.writeToTrace($"Skipping processNoteCreate for: {fullName}"
                                                                                                , tracingService);
                    return;
                }
                
                
                EntityReference annotationParentRef = annotation.GetAttributeValue<EntityReference>("objectid");

                if (annotationParentRef == null)
                    return;


                if (annotationParentRef.LogicalName != "incident")
                    return;

                Entity parentCase = service.Retrieve("incident", annotationParentRef.Id, new ColumnSet(true));

                string title = parentCase.GetAttributeValue<string>("title");


                int sourceChannelValue = parentCase.GetAttributeValue<OptionSetValue>("caseorigincode")?.Value ?? -1;


                AccountServicesHelper.writeToTrace($"sourceChannelValue: {sourceChannelValue}"
                                                                                             , tracingService);


                if (sourceChannelValue != 100003) //Formstack
                    return;

                dynamic globalSupportConfig = AccountServicesHelper.getConfigFromFieldMapping("GlobalSupport"
                                                                                                            , service, tracingService);

                string globalSupportConfigText = JsonConvert.SerializeObject(globalSupportConfig, Newtonsoft.Json.Formatting.Indented);
                IDictionary<string, System.Object> globalSupportConfigDict = JsonConvert.DeserializeObject<ExpandoObject>(globalSupportConfigText) as IDictionary<string, System.Object>;


                string generalQueue = globalSupportConfig.globalSupport.generalQueue;
                string securityRole = globalSupportConfig.globalSupport.securityRole;


                string noteDesc = annotation.GetAttributeValue<string>("notetext");

                string country = AccountServicesHelper.regexMatchValue(@"(?<=Country:\s)(\w+\s)*\w+(?=\n)", noteDesc, 0);

                AccountServicesHelper.writeToTrace($"processGlobalSupportCaseCreate(). Country value extracted from the note: {country}"
                                                                                                                                        , tracingService);

                dynamic countryMappingResponse = null;
                if (string.IsNullOrEmpty(country))
                    countryMappingResponse = AccountServicesHelper.identifyCountryRegionInName(title
                                                                                                        , service, tracingService);


                if (!string.IsNullOrEmpty(country) || countryMappingResponse?.valueCode != null)
                {

                    if (countryMappingResponse?.valueCode == null)
                    {
                        bool isCountryGroup = AccountServicesHelper.isOneWordAllCapitals(country);
                        countryMappingResponse = AccountServicesHelper.findCountryRegion(country, country, "ts_countryregionglobalsupport", service, tracingService, isCountryGroup);
                    }


                    if (countryMappingResponse?.valueCode != null)
                    {
                        parentCase["ts_countryregionglobalsupport"] = new OptionSetValue((int)countryMappingResponse.valueCode);
                        parentCase["ts_countrycode"] = (string)countryMappingResponse.value;

                        string groupName = "";
                        dynamic formstackRoutingItem = null;
                        List<dynamic> formstackRouting = ((JArray)globalSupportConfig.formstackRouting)?.ToList<dynamic>();


                        


                        formstackRoutingItem = ((JArray)globalSupportConfig.formstackRouting).ToList<dynamic>().Where(routingItem =>
                                                                                                                                //routingItem.countryRegionCode == (string)countryMappingResponse.value
                                                                                                                                //&&
                                                                                                                                    ((JArray)routingItem.subjectElements)?.ToList<dynamic>()?.All(subjectElement => title.Contains((string)subjectElement))
                                                                                                                                    ?? false
                                                                                                
                                                                                                                     )?.FirstOrDefault();


                        
                        
                        if(formstackRoutingItem == null)
                            formstackRoutingItem = ((JArray)globalSupportConfig.formstackRouting)?.ToList<dynamic>().Where(
                                                                                                                            routingItem => routingItem.countryRegionCode == (string)countryMappingResponse.value
                                                                                                                        )?.FirstOrDefault();


                        if (formstackRoutingItem != null)
                        {
                            groupName = (string)formstackRoutingItem.groupName ?? "";
                        }
                        else
                        {
                            groupName = $"TS: CSP Customer Service {country}";

                            bool existsTeam = AccountServicesHelper.existsTeamName(groupName
                                                                                            , service, tracingService);

                            if (!existsTeam)
                                groupName = $"Partner: CSP Customer Service {country}";

                            if (formstackRouting == null)
                            {
                                formstackRouting = new List<dynamic>();
                                globalSupportConfigDict.Add("formstackRouting", formstackRouting);
                            }

                            IDictionary<string, System.Object> countryRegionRouting = new ExpandoObject() as IDictionary<string, System.Object>;

                            countryRegionRouting.Add("countryRegionCode", (string)countryMappingResponse.value);
                            countryRegionRouting.Add("groupName", groupName);

                            formstackRouting.Add(countryRegionRouting);

                            globalSupportConfigDict["formstackRouting"] = formstackRouting;

                            globalSupportConfigText = JsonConvert.SerializeObject(globalSupportConfigDict, Newtonsoft.Json.Formatting.Indented);
                            bool success = AccountServicesHelper.updateConfigOnFieldMapping("GlobalSupport", globalSupportConfigText
                                                                                                                                    , service, tracingService);
                        }

                        if (!string.IsNullOrEmpty(groupName))
                        {
                            Guid teamId = AccountServicesHelper.getTeamIdForGroup(groupName
                                                                                        , service, tracingService);

                            if (teamId != Guid.Empty)
                            {
                                bool success = AccountServicesHelper.createTeamRoleAssoc(teamId, securityRole
                                                                                                            , service, tracingService);

                                if (success)
                                    parentCase["ownerid"] = new EntityReference("team", teamId);
                            }
                        }

                        service.Update(parentCase);
                    }

                    
                }


                string emailRegexPattern = @"[A-Za-z0-9_%+-]+(?:\.[A-Za-z0-9_%+-]+)*@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}";
                

                string emailAddressCustomerProvided = AccountServicesHelper.regexMatchValue(@"(?<=Email:\s)" + emailRegexPattern, noteDesc, 0);


                if (!string.IsNullOrEmpty(emailAddressCustomerProvided))
                {
                    parentCase["ts_emailaddresscustomerprovided"] = emailAddressCustomerProvided;

                    EntityReference customerRef = AccountServicesHelper.resolveCustomerForEmail(emailAddressCustomerProvided
                                                                                                                        , service, tracingService);

                    if (customerRef != null)
                        parentCase["customerid"] = customerRef;

                    service.Update(parentCase);
                }
            }
            catch (Exception e)
            {
                AccountServicesHelper.writeToTrace($"Error in processNoteCreate(). Exception message:{Environment.NewLine}{e.Message}"
                                                        + $"{Environment.NewLine}annotationId: {annotation.Id.ToString()}"
                                                        , tracingService);
                    
            }


        }
        public static void validateSystemNoteOwner(Entity annotation
                                                                , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService)
        {
            try
            {
                
                string noteText = annotation.GetAttributeValue<string>("notetext");


                string matchNoteDir = AccountServicesHelper.regexMatchValue("\\{\"sectionStart\"(\n|.)+\"NoteSpecialDirectives\"\\}$", noteText, 0);

                bool isSystemNote = false;
                if (!string.IsNullOrEmpty(matchNoteDir))
                {
                    var noteDirectives = JsonConvert.DeserializeObject<System.Dynamic.ExpandoObject>(matchNoteDir) as IDictionary<string, Object>;

                    if (noteDirectives != null && noteDirectives.ContainsKey("systemNote"))
                        isSystemNote = (bool)noteDirectives["systemNote"];

                    if (isSystemNote)
                    {
                        EntityReference ownerRef = annotation.GetAttributeValue<EntityReference>("ownerid");
                        Guid ownerId = ownerRef == null ? Guid.Empty : ownerRef.Id;

                        Guid userSystemId = AccountServicesHelper.getUserIdByFullName("SYSTEM"
                                                                                            , service, tracingService);


                        AccountServicesHelper.writeToTrace($"validateSystemNoteOwner(). ownerId:{ownerId.ToString()}; userSystemId: {userSystemId.ToString()}"
                                                                                                                                                            , tracingService);

                        if (ownerId != userSystemId)
                        {
                            annotation["ownerid"] = new EntityReference("systemuser", userSystemId);
                            service.Update(annotation);
                        }
                    }
                }

            }
            catch (Exception e)
            {
                AccountServicesHelper.writeToTrace($"Error in validateSystemNoteOwner(). Exception message:{Environment.NewLine}{e.Message}"
                                                        + $"{Environment.NewLine}annotationId: {annotation.Id.ToString()}"
                                                        , tracingService);
            }


        }
        
    }
}