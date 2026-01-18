
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
     
    public class NotePreCreateUpdate : IPlugin
    {
        static TimeZoneInfo pstZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        static Dictionary<string, string> EnvVariables;

        public void Execute(IServiceProvider serviceProvider)
        {
            
            ITracingService tracingService =
                (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            
            IPluginExecutionContext context = (IPluginExecutionContext)
                serviceProvider.GetService(typeof(IPluginExecutionContext));




            AccountServicesHelper.writeToTrace("Starting - AccountServices.NotePreCreateUpdate", tracingService);



            try
            {
                IOrganizationServiceFactory serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
                IOrganizationService service = serviceFactory.CreateOrganizationService(null);

                EnvVariables = AccountServicesHelper.GetEnvironmentVariables(service, tracingService);


                Entity annotation = null;
                Entity annotationTargetEntity = null;
                if (context.MessageName.Equals("Delete", StringComparison.OrdinalIgnoreCase))
                {
                    EntityReference annotationTargetRef = (EntityReference)context.InputParameters["Target"];

                    if (annotationTargetRef.LogicalName != "annotation")
                        return;

                    annotation = service.Retrieve("annotation", annotationTargetRef.Id, new ColumnSet(true));

                }
                else if (context.MessageName.Equals("Update", StringComparison.OrdinalIgnoreCase))
                {
                    annotationTargetEntity = (Entity)context.InputParameters["Target"];

                    if (annotationTargetEntity.LogicalName != "annotation")
                        return;

                    annotation = service.Retrieve("annotation", annotationTargetEntity.Id, new ColumnSet(true));
                }


                AccountServicesHelper.writeToTrace("annotation.Id: " + annotation.Id.ToString(), tracingService);


                if (
                    context.MessageName.Equals("Update", StringComparison.OrdinalIgnoreCase)
                      || context.MessageName.Equals("Delete", StringComparison.OrdinalIgnoreCase)
                      )
                    validateAnnotationUpdateDelete(annotation
                        , context, service, tracingService);



                //if (context.MessageName.Equals("Update", StringComparison.OrdinalIgnoreCase))
                //    preventSystemNoteOwnerChange(annotation, annotationTargetEntity
                //        , context, service, tracingService);
            }

            catch (Exception e)
            {
                string errorType = e.GetType().ToString();
                if (errorType.EndsWith("InvalidPluginExecutionException"))
                    throw new InvalidPluginExecutionException(e.Message);

                AccountServicesHelper.writeToTrace("Error during AccountServices.NotePreCreateUpdate: " + e.Message, tracingService);
            }

        }


        public static void validateAnnotationUpdateDelete(Entity annotation
            , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService)
        {
            try
            {
                Entity user = service.Retrieve("systemuser", context.InitiatingUserId, new ColumnSet(true));
                string fullName = user.GetAttributeValue<string>("fullname");


                AccountServicesHelper.writeToTrace("validateAnnotationUpdateDelete InitiatingUserId: " + fullName, tracingService);

                if (fullName.Contains("TSDynamics") || fullName.Contains("TSDynamicsOnyx") || fullName.Contains("DynamicsClient") || fullName.Contains("DynamicsESBIntegration") || fullName.Contains("SYSTEM"))
                    return;

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
                        throw new InvalidPluginExecutionException("This is a system note, and cannot be updated or deleted");
                    }
                }

            }
            catch (Exception e)
            {
                string errorType = e.GetType().ToString();
                if (errorType.EndsWith("InvalidPluginExecutionException"))
                    throw new InvalidPluginExecutionException(e.Message);

                AccountServicesHelper.writeToTrace("Error in validateAnnotationUpdateDelete(...). Exception message: " + Environment.NewLine + e.Message
                    + Environment.NewLine + "annotationId: " + annotation.Id.ToString()
                    , tracingService);
            }

        }

        public static void preventSystemNoteOwnerChange(Entity annotation, Entity annotationTargetEntity
            , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService)
        {

            try
            {
                Entity user = service.Retrieve("systemuser", context.InitiatingUserId, new ColumnSet(true));
                string fullName = user.GetAttributeValue<string>("fullname");


                AccountServicesHelper.writeToTrace("validateAnnotationUpdate InitiatingUserId: " + fullName, tracingService);



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
                        if (annotationTargetEntity.Contains("ownerid"))
                        {
                            EntityReference targetOwnerRef = annotationTargetEntity.GetAttributeValue<EntityReference>("ownerid");
                            Guid targetOwnerId = targetOwnerRef == null ? Guid.Empty : targetOwnerRef.Id;

                            EntityReference currentOwnerRef = annotation.GetAttributeValue<EntityReference>("ownerid");
                            Guid currentOwnerId = currentOwnerRef == null ? Guid.Empty : currentOwnerRef.Id;

                            AccountServicesHelper.writeToTrace("This is a system note"
                                + Environment.NewLine + "currentOwnerId: " + currentOwnerId + "; targetOwnerId: " + targetOwnerId
                                , tracingService);

                            if (targetOwnerId != currentOwnerId)
                                throw new InvalidPluginExecutionException("This is a system note, and cannot be updated or deleted");

                        }                        
                    }
                }

            }
            catch (Exception e)
            {
                string errorType = e.GetType().ToString();
                if (errorType.EndsWith("InvalidPluginExecutionException"))
                    throw new InvalidPluginExecutionException(e.Message);

                AccountServicesHelper.writeToTrace("Error in validateAnnotationUpdate(...). Exception message: " + Environment.NewLine + e.Message
                    + Environment.NewLine + "annotationId: " + annotation.Id.ToString()
                    , tracingService);
            }

        }


        

    }
}