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
using Newtonsoft.Json.Linq;
using System.Net.NetworkInformation;
using Newtonsoft.Json;
using System.Dynamic;

namespace DynamicsProcesses
{
    internal class IRSRevocationProcess
    {
        private static readonly string IRS_REVOCATION_URL = "http://apps.irs.gov/pub/epostcard/data-download-revocation.zip";
        private static readonly string SERVER_FOLDER_PATH = @"\\pdrptdb2k14.prod.compumentor.org\c$\IRSRevocationListImport\IRSRevocationFiles";
        private static readonly string SERVER_IMPORT_FOLDER_PATH = @"\\pdrptdb2k14.prod.compumentor.org\c$\IRSRevocationListImport\Import";
        private static readonly string REVOCATION_FILE_NAME = "data-download-revocation.txt";
        private static readonly string SQL_SERVER = "pdrptdb2k14.prod.compumentor.org";
        private static readonly string DATABASE_NAME = "dbadmin";
        private static readonly string SSIS_PACKAGE_NAME = "ImportIRSRevocationList_SQL2014";

        public static IDictionary<string, Object> AutomatedValidationConfig;

        public static dynamic AutomatedValDefinition;

        public static void processIRSRevocation()
        {
            try
            {

                var automatedValSettings = DynamicsProcessesAutomatedValidation.getAutomatedValidationConfig();

                AutomatedValidationConfig = JsonConvert.DeserializeObject<ExpandoObject>(automatedValSettings.automatedValConfigText) as IDictionary<string, Object>;

                AutomatedValDefinition = JsonConvert.DeserializeObject(automatedValSettings.automatedValConfigText);



                string processesCsv = "";
                if (DynamicsInterface.Args.Length > 1)
                    processesCsv = DynamicsInterface.Args[1];




                string[] processes = processesCsv.Split(',');

                foreach(string process in processes)
                {
                   switch(process.ToLower())
                    {
                        case "importlist":
                            importIRSRevocationList();
                            break;
                        case "irsdiqualification":
                            processOrgIrsDiqualification();
                            break;
                        //default:
                        //    DynamicsInterface.writeToLog($"Unknown process: {process}");
                        //    break;
                    }
                }



                //importIRSRevocationList();
                //processOrgIrsDiqualification();
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog($"Error in processIRSRevocation(): {e.Message}");
            }
        }



        public static void importIRSRevocationList()
        {
            try
            {
                DynamicsInterface.writeToLog("Starting IRS Revocation List import...");

                
                downloadAndExtractIRSRevocationFile();                

                
                executeSSISPackageOnServer();



                DynamicsInterface.writeToLog("IRS Revocation List import completed successfully.");
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog($"Error in importIRSRevocationList(): {e.Message}");
                
            }
        }

        private static void downloadAndExtractIRSRevocationFile()
        {
            try
            {
                
                if (!Directory.Exists(SERVER_FOLDER_PATH))
                {
                    Directory.CreateDirectory(SERVER_FOLDER_PATH);
                    DynamicsInterface.writeToLog($"Created directory: {SERVER_FOLDER_PATH}");
                }

                if (!Directory.Exists(SERVER_IMPORT_FOLDER_PATH))
                {
                    Directory.CreateDirectory(SERVER_IMPORT_FOLDER_PATH);
                    DynamicsInterface.writeToLog($"Created directory: {SERVER_IMPORT_FOLDER_PATH}");
                }

                string tempZipPath = Path.GetTempFileName() + ".zip";
                string serverFilePath = Path.Combine(SERVER_FOLDER_PATH, REVOCATION_FILE_NAME);

                
                using (HttpClient httpClient = new HttpClient()) // Download the zip file to temp location first
                {
                    httpClient.Timeout = TimeSpan.FromMinutes(10); // Set timeout for large files
                    DynamicsInterface.writeToLog($"Downloading IRS revocation file from: {IRS_REVOCATION_URL}");

                    using (HttpResponseMessage response = httpClient.GetAsync(IRS_REVOCATION_URL).Result)
                    {
                        response.EnsureSuccessStatusCode();

                        using (FileStream fileStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write))
                        {
                            response.Content.CopyToAsync(fileStream).Wait();
                        }
                    }

                    DynamicsInterface.writeToLog($"Downloaded zip file to temp location");
                }
                
                
                using (ZipArchive archive = ZipFile.OpenRead(tempZipPath)) // Extract the specific file from the zip directly to server location
                {
                    var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals(REVOCATION_FILE_NAME, StringComparison.OrdinalIgnoreCase));
                    
                    if (entry != null)
                    {
                        
                        if (File.Exists(serverFilePath)) // Delete existing file if it exists
                        {
                            File.Delete(serverFilePath);
                        }

                        entry.ExtractToFile(serverFilePath);
                        DynamicsInterface.writeToLog($"Extracted file to server location: {serverFilePath}");
                    }
                    else
                    {
                        DynamicsInterface.writeToLog($"File '{REVOCATION_FILE_NAME}' not found in the downloaded zip archive.");
                    }
                }

                
                if (File.Exists(tempZipPath)) // Clean up temp zip file
                {
                    File.Delete(tempZipPath);
                    DynamicsInterface.writeToLog("Cleaned up temporary zip file.");
                }

               
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog($"Error downloading and extracting IRS revocation file: {e.Message}");
                
            }
        }

        private static void executeSSISPackageOnServer()
        {
            try
            {
                DynamicsInterface.writeToLog("Starting SSIS package execution on SQL Server for IRS revocation data import...");

                string connectionString = $"Server={SQL_SERVER};Database=msdb;Integrated Security=true;Connection Timeout=30;";
                
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    DynamicsInterface.writeToLog($"Connected to SQL Server {SQL_SERVER} successfully");

                    // Execute the SSIS package directly from file using dtexec
                    string packageFileToExecute = @"C:\IRSRevocationListImport\Import\ImportIRSRevocationList.dtsx";
                    string dtexecCommand = $@"dtexec
                            /File ""C:\IRSRevocationListImport\Import\Import IRS List.dtsx"" 
                            /Connection ""DestinationConnectionOLEDB;Data Source=pdrptdb2k14.prod.compumentor.org;Initial Catalog=DBAdmin;Provider=SQLNCLI11;Integrated Security=SSPI;Auto Translate=false;"" 
                            /Connection ""SourceConnectionFlatFile;C:\IRSRevocationListImport\IRSRevocationFiles\data-download-revocation.txt""
                            ";
                    dtexecCommand = DynamicsProcessesHelper.regexReplace(@"\r\n\t*", dtexecCommand, " "); // Normalize command line
                    dtexecCommand = DynamicsProcessesHelper.regexReplace(@"(\s|\t)+", dtexecCommand," "); // Remove any tabs

                    DynamicsInterface.writeToLog($"Executing SSIS package with command: {dtexecCommand}");
                    
                    
                    string executePackageSQL = $@"
                       Declare @cmd nvarchar(4000)
                        Set @cmd = '{dtexecCommand}'
                        Exec xp_cmdshell @cmd
                        ";

                    using (SqlCommand command = new SqlCommand(executePackageSQL, connection))
                    {
                        command.CommandTimeout = 600; // 10 minutes timeout
                        SqlDataReader reader = command.ExecuteReader();
                        
                        // Read the output from dtexec
                        List<string> output = new List<string>();
                        while (reader.Read())
                        {
                            if (!reader.IsDBNull(0))
                            {
                                string line = reader.GetString(0);
                                if (!string.IsNullOrEmpty(line))
                                {
                                    output.Add(line);
                                }
                            }
                        }
                        
                        reader.Close();
                        
                        // Log the output
                        foreach (string line in output)
                        {
                            DynamicsInterface.writeToLog($"SSIS Output: {line}");
                        }
                        
                        // Check if execution was successful
                        bool successful = output.Any(line => line.Contains("DTExec: The package execution returned DTSER_SUCCESS"));
                        
                        if (successful)
                        {
                            DynamicsInterface.writeToLog("SSIS package executed successfully");
                        }
                        else
                        {
                            DynamicsInterface.writeToLog("SSIS package execution did not complete successfully. Check the output above for details.");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog($"Error executing SSIS package on server: {e.Message}");
            }
        }


        public static void processOrgIrsDiqualification()
        {
            try
            {
                string connString = "Data Source=" + "pdrptdb2k14.prod.compumentor.org" + ";Initial Catalog=DBAdmin;Integrated Security=True;Encrypt=False";
                DBAdminDataContext context = new DBAdminDataContext(connString);
                IEnumerable<usp_getOrgsOnIRSRevokeResult> irsQuery = null;
                //context.CommandTimeout = 100000;
                context.Connection.Open();

                irsQuery = from orgquals in context.usp_getOrgsOnIRSRevoke()
                           select orgquals;
                List<usp_getOrgsOnIRSRevokeResult> revokeResult = irsQuery.ToList<usp_getOrgsOnIRSRevokeResult>();




                var einElements = revokeResult.ToList().Select(org =>
                                                                    new XElement("value", org.normalizedEIN.ToString())
                                                                ).ToList();
                XElement tsEinCondition = new XElement("condition",
                                                                    new XAttribute("attribute", "ts_ein")
                                                                    , new XAttribute("operator", "in")
                                                                );
                tsEinCondition.Add(einElements);



                string reinstatedFetchExpression = @"
                                            <fetch version=""1.0"" output-format=""xml-platform"" mapping=""logical"" returntotalrecordcount=""true"" page=""1"" count=""5000"" no-lock=""false"">
	                                                <entity name=""ts_reinstatedorgs"">
		                                                <attribute name=""ts_ein""/>
		                                                <attribute name=""ts_dateadded""/>
	                                                </entity>
                                            </fetch>
                                            ";


                XDocument reinstatedFetchXmlDoc = XDocument.Parse(reinstatedFetchExpression);

                XElement reinstatedEntityElement = reinstatedFetchXmlDoc.Descendants("entity").FirstOrDefault();
                string reinstatedEntityName = reinstatedEntityElement.Attributes("name").FirstOrDefault().Value;
                XElement reinstatedFilterElement = reinstatedEntityElement.Descendants().ToList().Find(element => element.Name == "filter");
                if (reinstatedFilterElement == null)
                {
                    reinstatedEntityElement.Add(
                                        new XElement("filter",
                                                new XAttribute("type", "and")
                                                )
                                        );

                    reinstatedFilterElement = reinstatedEntityElement.Descendants().Where(element => element.Name == "filter").First();
                }
                reinstatedFilterElement.Add(tsEinCondition);


                reinstatedFetchExpression = reinstatedFetchXmlDoc.ToString();

                EntityCollection reinstatedOrgsCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(new FetchExpression(reinstatedFetchExpression));








                var tsorgIdElements = revokeResult.ToList().Select(org =>
                                                                        new XElement("value", org.iCompanyId.ToString())
                                                                        ).ToList();

                XElement accountNumberCondition = new XElement("condition",
                                                                    new XAttribute("attribute", "accountnumber")
                                                                    , new XAttribute("operator", "in")
                                                                );
                accountNumberCondition.Add(tsorgIdElements);

                string accountNumberConditionSerial = accountNumberCondition.ToString();



                string fetchExpressionQuery = @"
                                            <fetch version=""1.0"" output-format=""xml-platform"" mapping=""logical"" returntotalrecordcount=""true"" page=""1"" count=""50"" no-lock=""false"">
	                                                <entity name=""account"">
		                                                <attribute name=""accountid""/>
		                                                <attribute name=""name""/>
                                                        <attribute name=""new_orgdesignation""/>
                                                        <attribute name=""accountnumber""/>
                                                        <attribute name=""new_legalidentifier""/>

                                                        <filter type=""and"">
                                                            <condition attribute=""new_orgdesignation"" operator=""not-null""/>
		                                                </filter>

                                                        <link-entity name=""ts_organizationqualification"" alias=""orgqual"" link-type=""inner"" from=""ts_accountid"" to=""accountid"">
			                                                <attribute name=""ts_qualificationcodeid""/>
			                                                <attribute name=""ts_qualificationstatus""/>

                                                            <filter type=""and"">
                                                                <condition attribute=""ts_qualificationcodeid"" operator=""not-null""/>
                                                            </filter>
                                                        </link-entity>
	                                                </entity>
                                            </fetch>
                                            ";



                XDocument fetchXmlDoc = XDocument.Parse(fetchExpressionQuery);

                XElement entityElement = fetchXmlDoc.Descendants("entity").FirstOrDefault();
                string entityName = entityElement.Attributes("name").FirstOrDefault().Value;


                if (entityName != "account")
                    return;


                fetchXmlDoc.Descendants().FirstOrDefault().Attribute("count").Value = "5000";

                XElement filterElement = entityElement.Descendants().ToList().Find(element => element.Name == "filter");

                if (filterElement == null)
                {
                    entityElement.Add(
                                        new XElement("filter",
                                                new XAttribute("type", "and")
                                                )
                                        );

                    filterElement = entityElement.Descendants().Where(element => element.Name == "filter").First();
                }
                filterElement.Add(accountNumberCondition);



                string orgsInIRSRevokeListFetchExpression = fetchXmlDoc.ToString();  

                EntityCollection accountIRSRevokeListCollection = DynamicsInterface.DataverseClient.RetrieveMultiple(new FetchExpression(orgsInIRSRevokeListFetchExpression));

                int initial = accountIRSRevokeListCollection.Entities.Count;



                var irsDisQualNotReinstated = accountIRSRevokeListCollection.Entities.ToList().Where(org =>
                                                                                                            !reinstatedOrgsCollection.Entities.ToList().Exists(orgReinstated =>
                                                                                                                                                                    orgReinstated.GetAttributeValue<string>("ts_ein") == org.GetAttributeValue<string>("new_legalidentifier")
                                                                                                                                                                 //&& orgReinstated.GetAttributeValue<DateTime>("ts_dateadded") >= revokeResult.Where(
                                                                                                                                                                 //                                                                                    orgRevoked => orgRevoked.normalizedEIN == org.GetAttributeValue<string>("new_legalidentifier")
                                                                                                                                                                 //                                                                                    ).Select(orgRevoked => orgRevoked.Revocation_Date).First()
                                                                                                                                                                 )
                                                                                                            );


                int irsDisQualNotReinstatedCount = irsDisQualNotReinstated.Count();

                int[] nonDisqualifiedOrgStatuses = { 4, 1, 3 };
                var orgsIrsDisqual = irsDisQualNotReinstated.Where(account =>
                                                                            account.GetAttributeValue<EntityReference>("new_orgdesignation").Id == ((EntityReference)account.GetAttributeValue<AliasedValue>("orgqual.ts_qualificationcodeid").Value).Id
                                                                            && nonDisqualifiedOrgStatuses.Contains(
                                                                                                                    (int)(
                                                                                                                        (OptionSetValue)(account.GetAttributeValue<AliasedValue>("orgqual.ts_qualificationstatus").Value)
                                                                                                                    ).Value
                                                                                                                    )
                                                                    );





                int orgsIrsDisqualCount = orgsIrsDisqual.Count();


                //List<string> irsDisqualTsOrgIdsList = orgsIrsDisqual.Select(org => org.GetAttributeValue<string>("accountnumber")).ToList();

                DateTime emptyDate = new DateTime(1900, 1, 1);
                string[] closingQualCaseStatuses = { "OQ - Qualified", "OQ - Disqualified", "OQ - Cancelled", "OQ - Closed", "OQ - Abandoned", "OQ - Expired" };
                foreach (Entity account in orgsIrsDisqual)
                {

                    string name = account.GetAttributeValue<string>("name");
                    Guid orgDesigId = account.GetAttributeValue<EntityReference>("new_orgdesignation").Id;
                    string tsOrgId = account.GetAttributeValue<string>("accountnumber");
                    EntityReference qualCodeRef = (EntityReference)account.GetAttributeValue<AliasedValue>("orgqual.ts_qualificationcodeid").Value;




                    Entity caseEntity = DynamicsProcessesHelper.getCaseEntity(
                                                                            caseTypeCode: 2 // Qualification Case
                                                                            , type: 101996 // Organization Qualification
                                                                            , accountId: account.Id
                                                                            , qualCodeId: orgDesigId
                                                                            , tsOrderId: null
                                                                            );



                    string tsCaseStatusText = caseEntity == null ? "" : caseEntity.FormattedValues["ts_casestatus"];

                    Guid caseId = Guid.Empty;

                    if (tsCaseStatusText == "" || closingQualCaseStatuses.Contains(tsCaseStatusText))
                    {

                        Entity qualCodeEntity = DynamicsInterface.DataverseClient.Retrieve("new_qualificationcode", orgDesigId, new ColumnSet(true));

                        string qualName = qualCodeEntity.GetAttributeValue<string>("new_qualname");
                        string qualCode = qualCodeEntity.GetAttributeValue<string>("new_qualcode");


                        caseId = DynamicsProcessesHelper.createCase(title: qualCode + " - " + qualName
                                                                        , caseTypeCode: 2 // Qualification Case
                                                                        , type: 101996 // Organization Qualification
                                                                        , caseStatus: 103982  //OQ - IRS Disqualified
                                                                        , accountId: account.Id
                                                                        , qualCodeId: orgDesigId
                                                                        , tsOrderId: null);
                    }
                    else
                    {
                        caseId = caseEntity.Id;
                        caseEntity["ts_casestatus"] = new OptionSetValue(103982); //103982 - OQ - IRS Disqualified	
                        DynamicsInterface.DataverseClient.Update(caseEntity);
                    }


                    string irsDisqualificationQueue = AutomatedValDefinition.config.irsDisqualificationQueue;

                    DynamicsProcessesHelper.addCaseToQueue(caseId, irsDisqualificationQueue);



                    string legalIdentifier = account.GetAttributeValue<string>("new_legalidentifier");
                    usp_getOrgsOnIRSRevokeResult orgRevokeDetails = revokeResult.Find(orgRevoked => orgRevoked.normalizedEIN == legalIdentifier);


                    string noteDesc = "";
                    if (orgRevokeDetails != null)
                    {
                        noteDesc += "Revocation Date: " + orgRevokeDetails.Revocation_Date.Value.ToString("MM/dd/yyyy") + Environment.NewLine;
                        noteDesc += "Revocation Posting Date: " + orgRevokeDetails.Revocation_Posting_Date.Value.ToString("MM/dd/yyyy") + Environment.NewLine;

                        string reinstatementDateFormat = orgRevokeDetails.Exemption_Reinstatement_Date.Value == emptyDate ? "" : orgRevokeDetails.Exemption_Reinstatement_Date.Value.ToString("MM/dd/yyyy");
                        noteDesc += "Exemption Reinstatement Date: " + reinstatementDateFormat;
                    }
                    string noteTitle = "Bulk IRS Revocation " + TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, DynamicsInterface.pstZone).ToString("MM.dd.yyyy") + ". EIN: " + legalIdentifier;
                    DynamicsProcessesHelper.processSystemNote(
                                                            noteTitle: noteTitle
                                                            , noteDesc: noteDesc
                                                            , annotationParentRef: new EntityReference("account", account.Id)
                                                            );

                   
                }

            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in processOrgIrsDiqualification(). Exception message: " + Environment.NewLine + e.Message);
            }
        }

        public static usp_getOrgIRSRevokeInfoResult getOrgIRSRevocationRecord(string ein)
        {
            usp_getOrgIRSRevokeInfoResult orgIrsRevokeRecord = null;
            try
            {
                DBAdminDataContext context = new DBAdminDataContext();
                IEnumerable<usp_getOrgIRSRevokeInfoResult> irsQuery = null;
                context.Connection.Open();

                irsQuery = from orgquals in context.usp_getOrgIRSRevokeInfo(ein)
                           select orgquals;
                List<usp_getOrgIRSRevokeInfoResult> revokeResult = irsQuery.ToList<usp_getOrgIRSRevokeInfoResult>();

                if (revokeResult.Count == 0)
                    return null;

                orgIrsRevokeRecord = revokeResult.First();


            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in getOrgIRSRevocationRecord(...). Exception message: " + Environment.NewLine + e.Message
                                                + Environment.NewLine + "EIN: " + ein
                                                );
            }

            return orgIrsRevokeRecord;
        }
    }
}







