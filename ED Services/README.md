__EDServicesRequest__

Custom API Plugin

*Functionality & Request Reference Report*

EDServices Codebase · May 2026 – Menua Sepoyan

# __Overview__

EDServicesRequest is a Microsoft Dataverse Custom API plugin \(IPlugin\) that serves as a unified service gateway for Equivalency Determination \(ED\) operations\. It is the single entry point for requests spanning three integration domains: Dataverse\-native processing, Box document management, and NetSuite ERP integration\.

All calls arrive via a single ts\_request input parameter containing a requestName field\. The plugin dispatches to the appropriate handler, collects errors into a shared error stack, and returns a ts\_response Entity with a resultStatus of 'success' or 'failure' along with typed output fields\.

At startup the plugin resolves the current Dynamics environment, maps it to a named tier \(dev / qa / stage / prod\), and loads all credentials and configuration from Dataverse environment variables via EDServicesHelper\.

## __Assembly & Plugin Inventory__

__Assembly__

EDServices\.dll \(targets \.NET Framework 4\.7\.1\)

__Namespace__

EDServices

__Key Plugins__

EDServicesRequest · BoxInterface · GetDocuments · GetDocumentContent

__Key Services__

EmbeddedBoxApiService · AccountMatchService · NetSuiteTokenGenerator · OAuthBase

__Box Auth__

Client Credentials Grant \(enterprise token, enterprise ID 555277\); hardcoded token fallback

__NetSuite Auth__

OAuth 1\.0a, HMAC\-SHA256; nonce sourced from ESB stored procedure usp\_GetNetSuiteNonce via certificate\-authenticated WCF

__Dynamics Auth__

Client credentials \(ts\_TSDynamicsClientId / ts\_TSDynamicsClientSecret\), tenant 

__Environments__

dev · qa · stage · prod \(detected at runtime from Dynamics organisation URL\)

## __Request Summary__

__Request Name__

__Category__

__Purpose__

edRequest

Dataverse \+ Box

The primary and most complex handler

getOrgQualStatus

Dataverse

Retrieves the current qualification status for a given organization

getNetSuiteToken

NetSuite

Generates an OAuth 1

netsuiteApiCall

NetSuite

Executes a live GET or POST HTTP call against a NetSuite REST endpoint, generating the required OAuth 1

uploadEdFile

Box \+ Dataverse

Uploads a file to the appropriate subfolder within the Box folder structure for a given ED Case

edFileList

Box \+ Dataverse

Returns a flat list of all files within the Box folder structure for a specific ED Case or across all ED Cases for an NGO

edFileListByCategory

Box \+ Dataverse

Returns files organized by folder/category rather than as a flat list

getEdFileContent

Box

Retrieves the binary content of a specific file from Box, returned as a base64\-encoded string

deleteEdFile

Box

Permanently deletes a file from Box by its file ID

getEdFileVersions

Box

Retrieves the version history of a specific file in Box, returning cleaned file metadata and a list of all available versions

getFileVersionContent

Box

Retrieves the binary content of a specific historical version of a Box file

findEdRequestCase

Dataverse

Checks whether an ED Request Case already exists for a specific Grantmaker under a parent ED Case

getEdDynamicsCaseIds

Dataverse

Given a list of TS Incident IDs, returns the corresponding Dataverse case GUIDs

findOrganizationMatches

Dataverse

Searches Dataverse for existing Account and Contact records that match an incoming organization profile

retrieveNgoFromEdRequest

Dataverse

Given a parent ED Case and a Grantmaker TSOrgId, returns the full profile of the NGO associated with that case

getPngoIdFromEd

Dataverse

Retrieves the TSOrgId of the PNGO administrative account linked to a given ED Case via the ts\_pngoaccountid field

testRequest

Dev / Test

An active switch case used during development

# __Request Details__

__1\. edRequest__*   Process ED Request*

__Category: __Dataverse \+ Box

The primary and most complex handler\. Orchestrates the full end\-to\-end workflow for registering a new Equivalency Determination \(ED\) request submitted by a Grantmaker on behalf of an NGO\.

__Inputs__

- organization \(Entity\) — NGO details; may include tsOrgId or require a match lookup
- grantMaker \(Entity\) — Grantmaker details with tsOrgId and a contact that must supply tsContactId
- dueDate \(string\) — Due date in PST; stored in Dataverse as UTC
- processInCtp \(bool/int\) — Whether to process in CTP
- checkBoardSanctions \(bool\) — Whether to run board sanctions checks
- edType \(string\) — Type of ED request
- productId \(string\) — Associated product identifier
- expedite \(bool, optional\) — Whether the ED case should be expedited
- preferredLanguage \(string, optional\) — Used for PNGO assignment

__Outputs__

- resultStatus: 'success'
- incidentId — ts\_tsincidentid of the parent ED Case
- dynamicsCaseId — Dataverse GUID of the parent ED Case
- pngoAdminId — TSOrgId of the PNGO linked to the ED Case
- ngoTsOrgId — Resolved TSOrgId of the NGO
- grantMakerTsOrgId — TSOrgId of the Grantmaker

__Processing Logic__

1. NGO Resolution: if tsOrgId is provided it retrieves the account directly; otherwise the AccountMatchService runs a weighted fuzzy match \(name 50%, address 30%, postal code 10%, state 10%, threshold ≥0\.50\)\. If no match is found, a new NGO account is created with country/state hierarchy mapping\.
2. Tags the NGO account as type 14 \(NGO\) in the ts\_accounttype multi\-select field\.
3. Iterates over the NGO's contacts, upserting each by email and linking them to the NGO account via a Staff/Contact connection role\.
4. Retrieves the Grantmaker account by tsOrgId, tags it as type 17 \(Grantmaker\), validates that the Grantmaker contact exists and has a tsContactId\.
5. ED Case retrieval/creation \(getEdCase state machine\): checks the NGO's NGOR\-EDApp qualification status and branches — 'ED \- In Progress' reuses the latest case; 'ED \- Denied' blocks the request entirely; 'ED \- Expired' or 'ED \- Approved' within 180 days of expiry creates a new case chained via ts\_previouscaseid/ts\_nextcaseid; 'ED \- Approved' with more than 180 days remaining reuses the latest case\. PNGO assignment uses a 4\-tier fallback: country\+language, country\-only, language\-only, global default\.
6. Checks for a duplicate ED Request Case: if a child case \(caseTypeCode 4\) already exists for the same Grantmaker under this ED Case, it returns an error\.
7. Creates the ED Request Case with status 102244 \(ED Requested\), sets all metadata fields, then clears ts\_originalcaseid, ts\_previouscaseid, and ts\_nextcaseid linkages on the new case\.
8. Provisions Box folder infrastructure: ensures the NGO folder, ED folder \(named \{year\}\_\{incidentId\}\_\{ngoName\}\), and 8 standard subfolders exist \(SYSTEM ADDED, NGO ADDED ORIGINALS, NGO ADDED SCANNED, FOR FILE, FOR REDACTION, FOR GRANTMAKER, and within FOR GRANTMAKER: SUPPORTING DOCUMENTS, ANALYSIS, SANCTION CHECKS\)\.
9. Links the Grantmaker account to the NGO account via an ED Requestor/ED Applicant connection\.
10. Returns the PNGO admin ID resolved from the parent ED Case\.

__2\. getOrgQualStatus__*   Get Organization Qualification Status*

__Category: __Dataverse

Retrieves the current qualification status for a given organization\. Looks up the account's new\_orgdesignation field, then queries the ts\_organizationqualification table for the NGOR\-EDApp qualification code, returning the formatted ts\_qualificationstatus value\.

__Inputs__

- tsOrgId \(string\) — TSOrgId of the organization to query

__Outputs__

- resultStatus: 'success'
- tsOrgId — Echoed back
- orgQualStatus — Formatted qualification status string \(e\.g\. 'ED \- Approved', 'ED \- In Progress'\)

__Processing Logic__

1. Validates that tsOrgId is provided\.
2. Retrieves the account by accountnumber, reads the new\_orgdesignation EntityReference\.
3. Queries ts\_organizationqualification filtered by qualificationcodeid and accountid, returning FormattedValues\['ts\_qualificationstatus'\]\.

__3\. getNetSuiteToken__*   Get NetSuite Authentication Token*

__Category: __NetSuite

Generates an OAuth 1\.0a authentication token for NetSuite API calls\. Supports both SOAP and REST call types\. Credentials are read from Dataverse environment variables at runtime\. The nonce for SOAP tokens is retrieved from the ESB via a certificate\-authenticated WCF call to usp\_GetNetSuiteNonce\.

__Inputs__

- netSuiteCallType \(string\) — 'soap' or 'rest'
- url \(string, REST only\) — The target NetSuite endpoint URL
- httpMethod \(string, REST only\) — HTTP method \(GET or POST\)

__Outputs__

- resultStatus: 'success'
- netSuiteCallType, accountId, consumerKey, tokenId — Credential identifiers
- SOAP: nonce, timeStamp, signature \(HMAC\-SHA256 over accountId&consumerKey&tokenId&nonce&timeStamp\)
- REST: authorization \(full OAuth 1\.0a Authorization header value using standard signature base string\)

__Processing Logic__

1. Reads ts\_NetSuiteAccountId, ts\_NetSuiteConsumerKey, ts\_NetSuiteTokenId, ts\_NetSuiteConsumerSecret, and ts\_NetSuiteTokenSecret from environment variables\.
2. For SOAP: calls NetSuiteTokenGenerator\.GetSOAPToken, which fetches the nonce from the ESB stored procedure, then computes HMAC\-SHA256 over the base string\.
3. For REST: calls NetSuiteTokenGenerator\.GetRESTSToken, which builds the standard OAuth 1\.0a signature base string \(sorted parameters\) and computes HMAC\-SHA256\. '\+' characters in the signature are percent\-encoded as %2B\.

__4\. netsuiteApiCall__*   Make NetSuite API Call*

__Category: __NetSuite

Executes a live GET or POST HTTP call against a NetSuite REST endpoint, generating the required OAuth 1\.0a Authorization header internally before making the request\.

__Inputs__

- url \(string\) — Target NetSuite REST endpoint URL
- httpMethod \(string\) — 'GET' or 'POST'
- netSuiteRequest \(string, POST only\) — JSON body payload

__Outputs__

- netSuiteResponse — Raw JSON response from NetSuite

__Processing Logic__

1. Validates url and httpMethod\.
2. POST: deserializes and re\-serializes the request body for formatting, generates the OAuth token, calls EDServicesHelper\.makeHttpPostCall with Authorization and User\-Agent: SuiteScript\-Call headers\.
3. GET: generates the OAuth token, constructs an HttpWebRequest directly and reads the response stream\.

__5\. uploadEdFile__*   Upload ED File to Box*

__Category: __Box \+ Dataverse

Uploads a file to the appropriate subfolder within the Box folder structure for a given ED Case\. If a file with the same name already exists, the Box API service creates a new version rather than replacing it \(versionUploadOnExistingFile: true\)\.

__Inputs__

- incidentId \(string\) — ts\_tsincidentid of the target ED Case
- category \(string\) — Target Box subfolder name \(e\.g\. 'SYSTEM ADDED', 'FOR GRANTMAKER'\)
- fileName \(string\), fileSize \(int\), contentType \(string\), fileContent \(string, base64\)
- uploaderEmail \(string, optional\) — Stored as Box file metadata
- description \(string, optional\) — Stored as a descriptionTag: prefixed Box tag

__Outputs__

- resultStatus: 'success'
- fileId — Box file ID of the uploaded file

__Processing Logic__

1. Looks up the ED Case by ts\_tsincidentid, then queries ts\_externalsystemreference for the Box ED Folder ID \(referenceType 2\)\.
2. Traverses the Box folder tree: checks the ED folder's direct children first; if not found, checks the FOR GRANTMAKER subfolder\.
3. Calls fileUpload with versionUploadOnExistingFile: true, uploading base64\-decoded content as multipart form data\.

__6\. edFileList__*   Get ED File List \(Flat\)*

__Category: __Box \+ Dataverse

Returns a flat list of all files within the Box folder structure for a specific ED Case or across all ED Cases for an NGO\. Files with names matching the archived\-version pattern \(\_\\d\{14\}\.ext\) are automatically filtered out\. Results are sorted by folder name\.

__Inputs__

- incidentId \(string, optional\) — ts\_tsincidentid of the ED Case
- ngoId \(string, optional\) — TSOrgId of the NGO \(used when incidentId is not provided\)
- categories \(EntityCollection, optional\) — Category names to filter by

__Outputs__

- resultStatus: 'success'
- edFileList — EntityCollection with fileId, fileName, fileSize, createdOn, modifiedOn, contentType, category, folderPath, description, and optionally incidentId per file

__Processing Logic__

1. If ngoId is provided without incidentId, traverses all ED folders under the NGO's Box folder, resolving each to its ED Case to include the incidentId in each file entry\.
2. If incidentId is provided, queries the ED Case's Box ED Folder reference and calls getAllFilesInSubTree \(requestType: allFilesInSubTree\)\.
3. Applies category filter if categories are specified\. File descriptions are extracted from Box tags with the descriptionTag: prefix\.

__7\. edFileListByCategory__*   Get ED File List \(Grouped by Category\)*

__Category: __Box \+ Dataverse

Returns files organized by folder/category rather than as a flat list\. When scoped to an NGO, results are grouped first by ED Case, then by subfolder within each case\.

__Inputs__

- incidentId \(string, optional\) — ts\_tsincidentid of the ED Case
- ngoId \(string, optional\) — TSOrgId of the NGO
- categories \(EntityCollection, optional\) — Category filter

__Outputs__

- resultStatus: 'success'
- edFileList — EntityCollection where each entry has a category field and a categoryFiles EntityCollection of file entities \(no fileContent\)

__Processing Logic__

1. For a specific incidentId, calls getAllFolderFilesInSubTree \(requestType: allFolderFilesInSubTree\) which returns a tree of folders each containing their files\.
2. For ngoId, returns a collection where each entry holds an incidentId and a nested edFiles EntityCollection grouped by subfolder\.
3. The root ED folder itself is excluded from the folder list; only subfolders are returned\.

__8\. getEdFileContent__*   Get ED File Content*

__Category: __Box

Retrieves the binary content of a specific file from Box, returned as a base64\-encoded string\.

__Inputs__

- fileId \(string\) — Box file ID

__Outputs__

- resultStatus: 'success'
- fileId — Echoed back
- fileContent — Base64\-encoded file content

__Processing Logic__

1. Calls makeBoxItemBasedRequest with itemType: File \(no itemOperation\), which triggers standard file retrieval\.
2. Extracts and returns the fileContent from the fileDetails attribute of the Box response\.

__9\. deleteEdFile__*   Delete ED File from Box*

__Category: __Box

Permanently deletes a file from Box by its file ID\.

__Inputs__

- fileId \(string\) — Box file ID to delete

__Outputs__

- resultStatus: 'success'
- deletedFileId — The file ID that was deleted

__Processing Logic__

1. Calls deleteBoxItem with itemType: File and itemOperation: delete\.
2. Returns the deleted file ID on success\.

__10\. getEdFileVersions__*   Get ED File Version History*

__Category: __Box

Retrieves the version history of a specific file in Box, returning cleaned file metadata and a list of all available versions\.

__Inputs__

- fileId \(string\) — Box file ID

__Outputs__

- resultStatus: 'success'
- fileId — Echoed back
- fileDetails — File metadata with extraneous fields stripped \(fileContent, itemIndex, folderId, folderName, subFolderTree removed\)
- fileVersions — EntityCollection of version records

__Processing Logic__

1. Calls makeBoxItemBasedRequest with itemOperation: getVersions\.
2. Strips non\-essential fields from the fileDetails before returning to keep the response clean\.

__11\. getFileVersionContent__*   Get ED File Version Content*

__Category: __Box

Retrieves the binary content of a specific historical version of a Box file\.

__Inputs__

- fileId \(string\) — Box file ID
- versionId \(string\) — Specific version ID to retrieve

__Outputs__

- resultStatus: 'success'
- fileDetails — Entity with fileId, versionId, name, size, contentType, and fileContent \(base64\)

__Processing Logic__

1. Calls makeBoxItemBasedRequest with itemOperation: getVersionFileContent and the versionId\.
2. Constructs a clean fileDetails entity with only the relevant fields from the Box response\.

__12\. findEdRequestCase__*   Find ED Request Case*

__Category: __Dataverse

Checks whether an ED Request Case already exists for a specific Grantmaker under a parent ED Case\. Used by external systems to prevent duplicate request submissions before calling edRequest\.

__Inputs__

- incidentId \(string\) — ts\_tsincidentid of the parent ED Case
- grantMakerTsOrgId \(string\) — TSOrgId of the Grantmaker

__Outputs__

- resultStatus: 'success'
- tsIncidentId, grantMakerTsOrgId — Echoed back
- edRequestCaseExists \(bool\) — Whether a matching child ED Request Case was found

__Processing Logic__

1. Executes a FetchXML query joining the parent incident \(filtered by ts\_tsincidentid\) to its child cases, then to the child case's customer account filtered by the Grantmaker's accountnumber\.
2. Returns true if one or more matching records are found\.

__13\. getEdDynamicsCaseIds__*   Batch\-Resolve ED Incident IDs to Dynamics GUIDs*

__Category: __Dataverse

Given a list of TS Incident IDs, returns the corresponding Dataverse case GUIDs\. Used by external systems that need to map their own identifiers to Dynamics record IDs\.

__Inputs__

- incidentIds \(EntityCollection\) — Each entity must contain a 'value' attribute with a ts\_tsincidentid string

__Outputs__

- resultStatus: 'success'
- edDynamicsCaseIds — EntityCollection; each entry has incidentId and edDynamicsCaseId \(GUID string\)

__Processing Logic__

1. Extracts ID strings from the input collection\.
2. Dynamically builds a FetchXML query with an 'in' condition using XDocument/XElement to safely inject values\. Supports up to 5000 records per call\.
3. Returns the ts\_tsincidentid\-to\-GUID mapping for all matched cases\.

__14\. findOrganizationMatches__*   Find Organization Matches*

__Category: __Dataverse

Searches Dataverse for existing Account and Contact records that match an incoming organization profile\. Supports deduplication and pre\-population workflows in external intake systems\.

__Inputs__

- organization \(Entity\) — Includes orgName, phone, email, url, address, and a contacts EntityCollection \(each contact must have an 'email' field\)

__Outputs__

- resultStatus: 'success'
- organizations — EntityCollection of matching accounts with TSOrgId, name, phone, email, url, address
- contacts — EntityCollection of matching contacts with TSContactId, name, email, address

__Processing Logic__

1. Calls AccountMatchService\.getOrganizationMatchList which uses a two\-stage candidate retrieval: Dataverse Search API \(v2\.0, fuzzy\) with QueryExpression fallback, filtered to the same country\. Scoring weights: name 50%, address 30%, postal code 10%, state/province 10%\. Threshold: ≥0\.50\.
2. For each matched account, maps the full Dataverse account record to a clean organization entity\.
3. For each contact in the input, queries Dataverse by email address and maps any found record\.

__15\. retrieveNgoFromEdRequest__*   Retrieve NGO from ED Request Case*

__Category: __Dataverse

Given a parent ED Case and a Grantmaker TSOrgId, returns the full profile of the NGO associated with that case\. The Grantmaker relationship is validated as part of the FetchXML join before any data is returned\.

__Inputs__

- incidentId \(string\) — ts\_tsincidentid of the parent ED Case
- grantMakerTsOrgId \(string\) — Used to validate the Grantmaker relationship

__Outputs__

- resultStatus: 'success'
- incidentId, grantMakerTsOrgId — Echoed back
- organization — Entity with TSOrgId, orgName, phone, email, url, and a nested address entity \(all fields from the ngo\.\* aliased join\)

__Processing Logic__

1. Executes a FetchXML query with two link\-entities: one joining to child cases filtered by the Grantmaker accountnumber \(validates the relationship\), and one joining to the NGO customer account to retrieve its profile fields\.
2. Extracts aliased attribute values from the ngo\.\* join columns and maps them to a clean organization entity\.

__16\. getPngoIdFromEd__*   Get PNGO ID from ED Case*

__Category: __Dataverse

Retrieves the TSOrgId of the PNGO administrative account linked to a given ED Case via the ts\_pngoaccountid field\.

__Inputs__

- incidentId \(string\) — ts\_tsincidentid of the ED Case

__Outputs__

- resultStatus: 'success'
- incidentId — Echoed back
- pngoId — TSOrgId \(accountnumber\) of the PNGO account, or empty string if not linked

__Processing Logic__

1. Looks up the ED Case by ts\_tsincidentid\.
2. Reads the ts\_pngoaccountid EntityReference, retrieves the linked account, and returns its accountnumber\.

__17\. testRequest__*   Test Request \(Internal Use Only\)*

__Category: __Dev / Test

An active switch case used during development\. Exercises the organization resolution, contact upsert, and Grantmaker account\-tagging logic without creating ED Cases or Box folders\. The original stub body is commented out; the current active body runs real Dataverse writes\. Should be guarded or removed before production deployments\.

__Inputs__

- organization \(Entity\) — With optional tsOrgId and a contacts EntityCollection
- grantMaker \(Entity\) — With tsOrgId and optional contact

__Outputs__

- ngoTsOrgId, grantMakerTsOrgId — Resolved TSOrgIDs

__Processing Logic__

1. Resolves or creates the NGO account \(same path as edRequest\)\.
2. Tags the NGO as type 14 and upserts/links its contacts\.
3. Retrieves the Grantmaker account, tags it as type 17, optionally adds/links the Grantmaker contact\.
4. Does not create cases, Box folders, or external system references\.

# __Supporting Components__

## __BoxInterface Plugin__

A separate Custom API plugin \(ts\_BoxInterface\) that wraps the EmbeddedBoxApiService for direct calls from Dynamics JavaScript or Power Automate flows\. It accepts an Entity input, converts it to clean JSON by stripping @odata\.type elements, invokes EmbeddedBoxApiService\.ProcessBoxRequestForDynamics, and returns the result as a Dataverse Entity\. Authentication uses the same Client Credentials Grant flow as EDServicesRequest, with a hardcoded token as fallback\.

__EmbeddedBoxApiService — Supported Operations__

- SearchService / BoxNative — Box native full\-text search
- SearchService / BoxAIEnhanced — Box AI\-enhanced search with relevance scoring and AI insights
- List / allFilesInSubTree — Flat recursive file list for a folder tree
- List / allFolderFilesInSubTree — Folder\-grouped recursive file list
- Item / Create \+ Folder — Create a Box folder
- Item / Delete \+ Folder — Delete a Box folder
- File \+ upload \(itemOperation\) — Upload file with version\-on\-existing behaviour
- File \+ delete \(itemOperation\) — Delete a file
- File \+ rename \(itemOperation\) — Rename a file
- File \+ getVersions \(itemOperation\) — Retrieve version history
- File \+ getVersionFileContent \(itemOperation\) — Retrieve content of a specific version
- Folder \+ getVersions / rename / delete \(itemOperation\) — Folder\-level operations

## __AccountMatchService__

A weighted fuzzy\-matching service used by edRequest and findOrganizationMatches to locate existing Dataverse account records before creating new ones\.

__Candidate Retrieval \(two\-stage\)__

1. Primary: Dataverse Search API v2\.0 — fuzzy full\-text search filtered by country; results are re\-fetched from Dynamics via QueryExpression to get complete field sets\.
2. Fallback / supplement: QueryExpression matching on name, address, postal code, phone, and website fields\. Both results are deduplicated by accountid before scoring\.

__Scoring Algorithm__

Each candidate is scored across up to eight weighted fields\. When called from EDServicesRequest the weights applied are:

- Name: 50%  — Jaro\-Winkler \+ Levenshtein \+ cosine similarity, with legal\-suffix stripping \(Inc, Corp, LLC, Ltd, etc\.\)
- Address: 30%  — Normalised with common abbreviation expansion \(St→Street, Ave→Avenue, etc\.\)
- Postal Code: 10%  — Exact match
- State / Province: 10%  — Exact match
- Website, Legal ID, Phone: 0%  \(available in MatchConfiguration but set to 0 in this context\)

__Match threshold: __≥ 0\.50 overall score to qualify\. Results are sorted descending by score; when multiple matches exceed the threshold, the highest\-scoring match is used \(duplicate handling is noted as a TODO in the source\)\.

## __GetDocuments & GetDocumentContent__

Two standalone Custom API plugins for Dataverse\-native document management, operating independently of Box\.

__GetDocuments__

Accepts a numeric ts\_edid, looks up the associated incident, and retrieves all msdyn\_entityattachment records linked to it\. For each attachment it resolves file metadata \(MIME type, size, filename\) via the msdyn\_entityattachment\_FileAttachments relationship and returns a document list with name, category, type, size, modified date, and a direct Dynamics API URL\.

__GetDocumentContent__

Accepts a ts\_documentid \(msdyn\_entityattachment GUID\), acquires a Dynamics bearer token using client credentials, and streams the file binary from the Dynamics API \($value endpoint\), returning the content as a base64 string\.

## __NetSuite Token Generation__

NetSuiteTokenGenerator provides two static methods called by both getNetSuiteToken and netsuiteApiCall:

- GetSOAPToken: fetches a nonce from the ESB \(usp\_GetNetSuiteNonce via certificate\-authenticated WCF\), then computes HMAC\-SHA256 over accountId&consumerKey&tokenId&nonce&timeStamp using consumerSecret&tokenSecret as the key\.
- GetRESTSToken: builds a standard OAuth 1\.0a signature base string \(URL\-encoded, parameter\-sorted\), computes HMAC\-SHA256, URL\-encodes '\+' as %2B, and assembles the full Authorization: OAuth header\.

OAuthBase provides GenerateSignature256, GenerateTimeStamp \(Unix epoch seconds\), and GenerateNonce \(delegated to the ESB\)\. The nonce server address is read from a configuration key NonceServer\.

## __Box Folder Structure__

Box folder references are persisted in the ts\_externalsystemreference Dataverse table\. Each reference links a Dynamics record \(account or incident\) to a Box folder ID:

- referenceType 1 — NGO Folder: linked to the account record, named \{TSOrgId\}\_\{OrgName\}
- referenceType 2 — ED Folder: linked to the incident record, named \{year\}\_\{incidentId\}\_\{ngoName\}

When a new ED Case is created, processBoxFolderED creates the ED folder under the NGO folder if it does not yet exist, then processBoxEDSubfolders creates 8 standard subfolders:

- SYSTEM ADDED
- NGO ADDED ORIGINALS
- NGO ADDED SCANNED
- FOR FILE
- FOR REDACTION
- FOR GRANTMAKER  \(parent of the three below\)
	- SUPPORTING DOCUMENTS
	- ANALYSIS
	- SANCTION CHECKS

# __Implementation Notes__

## __Error Handling__

All handlers share a List<string> errorStack\. Errors are appended rather than thrown, allowing early\-exit detection after each external call \(errorStack\.Count > 0\)\. After the switch block, if the stack is non\-empty the ts\_response receives resultStatus: 'failure' and a concatenated error string\.

## __Environment & Credential Management__

All sensitive configuration \(NetSuite keys, Box folder IDs, Dynamics client credentials, ESB URL, SQL server names\) is stored in Dataverse environment variables and loaded at plugin startup\. The current environment tier is detected from the Dynamics organisation URL and stored in EDServicesHelper\.DynamicsEnvironments\['DynamicsEnvironmentCurrent'\]\.

## __NetSuite Dual\-Path Architecture__

Two mechanisms exist for NetSuite authentication: the public getNetSuiteToken request returns token components to the caller for use in external calls; the private getNetSuiteToken overload is used internally by makeNetSuiteCall to generate the Authorization header inline before making the HTTP request\. Both share the same NetSuiteTokenGenerator logic\.

## __File List Filtering__

When returning file lists, getAllFilesInSubTree silently excludes files whose names match the pattern \_\\d\{14\}\.ext \(e\.g\. \_20240315143022\.pdf\)\. These are timestamped archived versions created by a now\-commented\-out rename flow\. The pattern match uses EDServicesHelper\.regexMatch\.

## __testRequest Handler__

The testRequest case is active in the switch statement and performs real Dataverse writes \(account tagging, contact creation, connection records\)\. The original stub is commented out\. This handler should be gated behind an environment check or removed from the switch before production deployments\.

