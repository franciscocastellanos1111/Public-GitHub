
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




                if (
                    context.MessageName.Equals("Update", StringComparison.OrdinalIgnoreCase)
                      || context.MessageName.Equals("Create", StringComparison.OrdinalIgnoreCase)
                      )
                    validateSystemNoteOwner(annotation
                        , context, service, tracingService);


            }

            catch (Exception e)
            {
                AccountServicesHelper.writeToTrace("Error during AccountServices.NoteCreateUpdate: " + e.Message, tracingService);
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


                        AccountServicesHelper.writeToTrace("ownerId: " + ownerId.ToString() + "; userSystemId: " + userSystemId.ToString()
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
                AccountServicesHelper.writeToTrace("Error in preOperationAnnotationUpdate(...). Exception message: " + Environment.NewLine + e.Message
                                                                + Environment.NewLine + "annotationId: " + annotation.Id.ToString()
                                                                , tracingService);
            }


        }
        
    }
}