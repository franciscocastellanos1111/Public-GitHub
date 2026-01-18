
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

namespace AccountServices
{
     
    public class ConnectionPreCreateUpdate : IPlugin
    {
        static TimeZoneInfo pstZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        static Dictionary<string, string> EnvVariables;

        public void Execute(IServiceProvider serviceProvider)
        {
            
            ITracingService tracingService =
                (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            
            IPluginExecutionContext context = (IPluginExecutionContext)
                serviceProvider.GetService(typeof(IPluginExecutionContext));            



            tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "Starting - AccountServices.ConnectionPreCreateUpdate");
            tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));



            try
            {
                IOrganizationServiceFactory serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
                IOrganizationService service = serviceFactory.CreateOrganizationService(null);

                EnvVariables = AccountServicesHelper.GetEnvironmentVariables(service, tracingService);

                Entity connectionTargetEntity = null;

                connectionTargetEntity = (Entity)context.InputParameters["Target"];

                if (connectionTargetEntity.LogicalName != "connection")
                    return;



                tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "contactTargetEntity.Id: " + connectionTargetEntity.Id.ToString());
                tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));


                if (context.MessageName.Equals("Create", StringComparison.OrdinalIgnoreCase))
                {
                    validateConnectionCreate(connectionTargetEntity
                        , context, service, tracingService);

                    EntityReference connectionRoleFromRef = connectionTargetEntity.GetAttributeValue<EntityReference>("record1roleid");

                    Guid employerConnectionRoleId = connectionRoleFromRef == null ? Guid.Empty : connectionRoleFromRef.Id;


                    tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " 
                        + "After validateConnectionCreate. employerConnectionRoleId: " + employerConnectionRoleId.ToString()
                        );
                    tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));

                }
                if (context.MessageName.Equals("Update", StringComparison.OrdinalIgnoreCase))
                {
                    //Entity contact = service.Retrieve("contact", contactTargetEntity.Id, new ColumnSet(true));
                    //string tsContactId = contact.GetAttributeValue<string>("new_contactaccountnumber");


                }
            }

            catch (Exception e)
            {
               
                tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "Error during AccountServices.ConnectionPreCreateUpdate: " + e.Message);
                tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));
            }

        }


        public static void  validateConnectionCreate(Entity connectionTargetEntity
                                                                            , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService)
        {

            try
            {

                Entity user = service.Retrieve("systemuser", context.InitiatingUserId, new ColumnSet(true));
                string fullName = user.GetAttributeValue<string>("fullname");

                tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "validateConnectionCreate InitiatingUserId: " + fullName);
                tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));

                if (fullName.Contains("TSDynamicsOnyx") || fullName.Contains("DynamicsClient") || fullName.Contains("DynamicsESBIntegration"))
                    return;


                EntityReference connectionFromRef = connectionTargetEntity.GetAttributeValue<EntityReference>("record1id");

                string tsOrgId = string.Empty;
                string contactFromEntity = string.Empty;
                string iCategoryId = string.Empty;
                if (connectionFromRef != null && connectionFromRef.LogicalName == "account")
                {
                    contactFromEntity = "account";
                    Entity account = service.Retrieve("account", connectionFromRef.Id, new ColumnSet("accountnumber"));
                    tsOrgId = account.GetAttributeValue<string>("accountnumber");
                    iCategoryId = "2";

                    tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "contactFromEntity: " + contactFromEntity + "; tsOrgId: " + tsOrgId);
                    tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));
                }

                EntityReference connectionRoleFromRef = connectionTargetEntity.GetAttributeValue<EntityReference>("record1roleid");

                if (connectionRoleFromRef == null && contactFromEntity == "account")
                {
                    QueryExpression queryConnectionFromRole = new QueryExpression("connectionrole");
                    queryConnectionFromRole.Criteria.AddCondition("name", ConditionOperator.Equal, "Employer");
                    EntityCollection connectionRoleFromCollection = service.RetrieveMultiple(queryConnectionFromRole);

                    if (connectionRoleFromCollection.Entities.Count > 0)
                    {
                        


                        Guid employerConnectionRoleId = connectionRoleFromCollection.Entities.First().Id;
                        connectionTargetEntity["record1roleid"] = new EntityReference("connectionrole", employerConnectionRoleId);

                        tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " 
                            + "Changed record1roleid to: " + employerConnectionRoleId.ToString()
                            );
                        tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));
                    }
                }
            }
            catch (Exception e)
            {              

                tracingService.Trace(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pstZone).ToString("yyyy-MM-dd HH:mm:ss") + ": " + "Error in validateConnectionCreate(...). Exception message: "
                    + Environment.NewLine + e.Message
                    + Environment.NewLine + "connectionTargetEntityId: " + connectionTargetEntity.Id.ToString()
                    );
                tracingService.Trace(string.Concat(Enumerable.Repeat("-", 100).ToArray()));
            }


        }
        
    }
}