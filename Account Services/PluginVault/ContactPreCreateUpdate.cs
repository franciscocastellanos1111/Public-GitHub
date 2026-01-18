
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
     
    public class ContactPreCreateUpdate : IPlugin
    {
        static TimeZoneInfo pstZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        static Dictionary<string, string> EnvVariables;

        public void Execute(IServiceProvider serviceProvider)
        {
            
            ITracingService tracingService =
                (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            
            IPluginExecutionContext context = (IPluginExecutionContext)
                serviceProvider.GetService(typeof(IPluginExecutionContext));            


            AccountServicesHelper.writeToTrace("Starting - AccountServices.ContactPreCreateUpdate"
                                                        , tracingService);



            try
            {
                IOrganizationServiceFactory serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
                IOrganizationService service = serviceFactory.CreateOrganizationService(null);

                EnvVariables = AccountServicesHelper.GetEnvironmentVariables(service, tracingService);

                Entity contactTargetEntity = null;

                contactTargetEntity = (Entity)context.InputParameters["Target"];

                if (contactTargetEntity.LogicalName != "contact")
                    return;


                AccountServicesHelper.writeToTrace("contactTargetEntity.Id: " + contactTargetEntity.Id.ToString()
                                                        , tracingService);


                if (context.MessageName.Equals("Create", StringComparison.OrdinalIgnoreCase))
                {
                    contactTargetEntity = validateContactCreate(contactTargetEntity
                                                                    , context, service, tracingService);
                }
                if (context.MessageName.Equals("Update", StringComparison.OrdinalIgnoreCase))
                {
                    Entity contact = service.Retrieve("contact", contactTargetEntity.Id, new ColumnSet(true));
                    string tsContactId = contact.GetAttributeValue<string>("new_contactaccountnumber");


                    validateContactUpdate(contactTargetEntity, contact, tsContactId
                                                , context, service, tracingService);
                }
            }

            catch (Exception e)
            {
                string errorType = e.GetType().ToString();
                if (errorType.EndsWith("InvalidPluginExecutionException"))
                    throw new InvalidPluginExecutionException(e.Message);


                AccountServicesHelper.writeToTrace("Error during AccountServices.ContactPreCreateUpdate: " + e.Message
                                                        , tracingService);
            }

        }


        public static Entity validateContactCreate(Entity contactTargetEntity
                                                        , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService)
        {

            try
            {

                Entity user = service.Retrieve("systemuser", context.InitiatingUserId, new ColumnSet(true));
                string fullName = user.GetAttributeValue<string>("fullname");


                AccountServicesHelper.writeToTrace("validateContactCreate InitiatingUserId: " + fullName
                                                        , tracingService);


                
                if (fullName.Contains("TSDynamics") || fullName.Contains("TSDynamicsOnyx") || fullName.Contains("DynamicsClient") || fullName.Contains("DynamicsESBIntegration") || fullName.Contains("SYSTEM"))
                    return contactTargetEntity;

                throw new InvalidPluginExecutionException("Creating Contacts in Dynamics is not allowed at this time");

                if (contactTargetEntity.Contains("ts_emailvalidationstatus"))
                {
                    OptionSetValue emailValidationStatusOption = contactTargetEntity.GetAttributeValue<OptionSetValue>("ts_emailvalidationstatus");
                    if (emailValidationStatusOption.Value != 3 && emailValidationStatusOption.Value != 4)//3 - Invalid; 4 - Valid
                        contactTargetEntity["ts_emailvalidationstatus"] = new OptionSetValue(1); //1 - Not Validated
                }
                else
                {
                    contactTargetEntity["ts_emailvalidationstatus"] = new OptionSetValue(1);
                }



            }
            catch (Exception e)
            {
                string errorType = e.GetType().ToString();
                if (errorType.EndsWith("InvalidPluginExecutionException"))
                    throw new InvalidPluginExecutionException(e.Message);


                AccountServicesHelper.writeToTrace("Error in validateContactCreate(...). Exception message: "
                                                        + Environment.NewLine + e.Message
                                                        + Environment.NewLine + "contactTargetEntityId: " + contactTargetEntity.Id.ToString()
                                                        , tracingService);

            }

            return contactTargetEntity;
        }
        public static Entity validateContactUpdate(Entity contactTargetEntity, Entity contact, string tsContactId
                                                        , IPluginExecutionContext context, IOrganizationService service, ITracingService tracingService)
        {
            try
            {

                Entity user = service.Retrieve("systemuser", context.InitiatingUserId, new ColumnSet(true));
                string fullName = user.GetAttributeValue<string>("fullname");


                AccountServicesHelper.writeToTrace("validateContactUpdate InitiatingUserId: " + fullName
                                                        , tracingService);



                if (fullName.Contains("TSDynamicsOnyx"))
                    return contactTargetEntity;


                OptionSetValue emailValidationStatusOption = contactTargetEntity.Contains("ts_emailvalidationstatus") ? contactTargetEntity.GetAttributeValue<OptionSetValue>("ts_emailvalidationstatus") : contact.GetAttributeValue<OptionSetValue>("ts_emailvalidationstatus");


                if (emailValidationStatusOption == null)
                {
                    contactTargetEntity["ts_emailvalidationstatus"] = new OptionSetValue(1);
                }
                else if (emailValidationStatusOption.Value != 3 && emailValidationStatusOption.Value != 4)//3 - Invalid; 4 - Valid
                {

                    contactTargetEntity["ts_emailvalidationstatus"] = new OptionSetValue(1); //1 - Not Validated
                }


            }
            catch (Exception e)
            {
                string errorType = e.GetType().ToString();
                if (errorType.EndsWith("InvalidPluginExecutionException"))
                    throw new InvalidPluginExecutionException(e.Message);


                AccountServicesHelper.writeToTrace("Error in validateContactUpdate(...). Exception message: "
                                                        + Environment.NewLine + e.Message
                                                        + Environment.NewLine + "contactTargetEntityId: " + contactTargetEntity.Id.ToString()
                                                        , tracingService);


            }

            return contactTargetEntity;
        }
    }
}