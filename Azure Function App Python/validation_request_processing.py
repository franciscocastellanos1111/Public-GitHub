import json
import asyncio
import os
import re
from datetime import datetime, timezone, timedelta
from typing import Optional, Dict, Any, List
import httpx


DYNAMICS_ENVIRONMENT = os.getenv("DYNAMICS_ENVIRONMENT") or "https://tsdynamicsqa.crm.dynamics.com"
CLIENT_ID = os.getenv("CLIENT_ID")
CLIENT_SECRET = os.getenv("CLIENT_SECRET")
TENANT_ID = os.getenv("ts_AzureTenantId")

TOKEN: Optional[str] = None
TOKEN_EXPIRATION: datetime = datetime.min.replace(tzinfo=timezone.utc)


async def get_ms_token_web_api() -> Optional[str]:
    global TOKEN, TOKEN_EXPIRATION
    try:
        url = f"https://login.microsoftonline.com/{TENANT_ID}/oauth2/v2.0/token"

        parameters = {
            "client_id": CLIENT_ID,
            "scope": f"{DYNAMICS_ENVIRONMENT}/.default",
            "client_secret": CLIENT_SECRET,
            "grant_type": "client_credentials",
        }

        headers = {
            "Accept": "application/json",
            "Content-Type": "application/x-www-form-urlencoded",
        }

        print(f"Requesting token from: {url}")
        print(f"Client ID: {CLIENT_ID}")
        print(f"Resource: {DYNAMICS_ENVIRONMENT}")

        async with httpx.AsyncClient() as client:
            response = await client.post(url, data=parameters, headers=headers)
            response_text = response.text

        print(f"Token response status: {response.status_code}")

        if response.status_code == 200:
            response_object = json.loads(response_text)
            token = response_object.get("access_token")

            if token:
                expires_in = response_object.get("expires_in", 3599)
                TOKEN = token
                TOKEN_EXPIRATION = datetime.now(timezone.utc) + timedelta(seconds=expires_in - 200)
                print(f"Successfully obtained access token: {token[:30]}...")
                print(f"Token expires on: {TOKEN_EXPIRATION.isoformat()}")
                return token
            else:
                print("No access_token in response")
                print(f"Response: {response_text}")
                return None
        else:
            print(f"Token request failed with status {response.status_code}")
            print(f"Response: {response_text}")
            return None

    except Exception as e:
        print(f"Error getting Dataverse access token via Web API: {str(e)}")
        return None


async def get_cached_token() -> Optional[str]:
    try:
        now = datetime.now(timezone.utc)
        if TOKEN and now < TOKEN_EXPIRATION:
            print("Using cached token")
            return TOKEN
        return await get_ms_token_web_api()
    except Exception as e:
        print(f"Error in get_cached_token: {e}")
        return None


async def general_dataverse_webapi_request(method: str, endpoint_path: str, req_obj: Optional[Dict] = None, additional_headers: Optional[Dict[str, str]] = None) -> Dict:
    token = await get_cached_token()
    if not token:
        print("Failed to obtain access token")
        return {"success": False}

    base_url = f"{DYNAMICS_ENVIRONMENT}/api/data/v9.2/"
    url = base_url + endpoint_path

    headers = {
        "OData-MaxVersion": "4.0",
        "OData-Version": "4.0",
        "Accept": "application/json",
        "Content-Type": "application/json; charset=utf-8",
        "Authorization": f"Bearer {token}",
    }

    if additional_headers:
        headers.update(additional_headers)

    try:
        async with httpx.AsyncClient() as client:
            if method in ("GET", "DELETE"):
                response = await client.request(method, url, headers=headers)

                if not response.is_success:
                    print(f"HTTP {response.status_code}: {response.text}")
                    return {"success": False, "status": response.status_code}

                if response.status_code == 204:
                    return {"success": True, "status": 204}

                return response.json()

            elif method in ("PATCH", "POST", "PUT"):
                response = await client.request(method, url, headers=headers, json=req_obj)

                if not response.is_success:
                    print(f"HTTP {response.status_code}: {response.text}")
                    return {"success": False, "status": response.status_code}

                if response.status_code == 204:
                    entity_id = None
                    odata_entity_id = response.headers.get("OData-EntityId")
                    if odata_entity_id:
                        match = re.search(r"\(([0-9a-fA-F-]+)\)", odata_entity_id)
                        if match:
                            entity_id = match.group(1)
                    return {"success": True, "status": 204, "entityId": entity_id}

                return response.json()

            else:
                print(f"Unsupported HTTP method: {method}")
                return {"success": False}

    except Exception as e:
        print(f"API request failed: {e}")
        return {"success": False}


async def find_case_by_transaction_id(transaction_id: str) -> Optional[Dict[str, Any]]:
    try:
        select_columns = ",".join([
            "incidentid",
            "ts_validationrequestlegalname",
            "ts_validationrequestaddressline1",
            "ts_validationrequestaddressother",
            "ts_validationrequestaddresscity",
            "ts_validationrequestaddressstateregion",
            "ts_validationrequestaddresspostalcode",
            "ts_validationrequestaddresscountryid",
            "ts_validationrequestemail",
            "ts_validationrequestphone",
            "ts_validationrequestwebsite",
            "ts_validationrequestmissionstatement",
            "ts_validationrequestoperatingbudget",
            "ts_validationrequestactivitycode",
            "ts_validationrequestagentfirstname",
            "ts_validationrequestagentlastname",
            "ts_validationrequestagentemail",
            "ts_validationrequestorgtype",
            "ts_validationrequestlegalidentifier",
            "_customerid_value",
        ])

        filter_clause = f"ts_validationrequesttransactionid eq '{transaction_id}'"

        endpoint_path = (
            f"incidents"
            f"?$select={select_columns}"
            f"&$expand=customerid_account($select=name)"
            f"&$filter={filter_clause}"
        )

        print(f"Searching for incident with transaction ID: {transaction_id}")
        result = await general_dataverse_webapi_request("GET", endpoint_path)

        if result.get("success") is False:
            print("No result returned from Dataverse")
            return None

        records = result.get("value", [])
        if not records:
            print(f"No incident found with ts_validationrequesttransactionid = '{transaction_id}'")
            return None

        record = records[0]
        print(f"Found incident: {record.get('incidentid')}")
        print(json.dumps(record, indent=4))
        return record

    except Exception as e:
        print(f"Error in find_case_by_transaction_id: {e}")
        return None


async def create_incident(fields: Dict[str, Any], select_columns: List[str] = None) -> Dict:
    try:
        endpoint_path = "incidents"
        additional_headers = {}

        if select_columns:
            endpoint_path += "?$select=" + ",".join(select_columns)
            additional_headers["Prefer"] = "return=representation"

        print("Creating incident record...")
        result = await general_dataverse_webapi_request("POST", endpoint_path, fields, additional_headers)

        if result.get("success") is False:
            print("Failed to create incident record")
            return result

        incident_id = result.get("incidentid") or result.get("entityId")
        print(f"Created incident: {incident_id}")
        return result

    except Exception as e:
        print(f"Error in create_incident: {e}")
        return {"success": False}


async def update_incident(incident_id: str, fields: Dict[str, Any], select_columns: List[str] = None) -> Dict:
    try:
        endpoint_path = f"incidents({incident_id})"
        additional_headers = {}

        if select_columns:
            endpoint_path += "?$select=" + ",".join(select_columns)
            additional_headers["Prefer"] = "return=representation"

        print(f"Updating incident: {incident_id}")
        result = await general_dataverse_webapi_request("PATCH", endpoint_path, fields, additional_headers)

        if result.get("success") is False:
            print(f"Failed to update incident {incident_id}")

        return result

    except Exception as e:
        print(f"Error in update_incident: {e}")
        return {"success": False}


async def delete_incident(incident_id: str) -> Dict:
    try:
        endpoint_path = f"incidents({incident_id})"

        print(f"Deleting incident: {incident_id}")
        result = await general_dataverse_webapi_request("DELETE", endpoint_path)

        if result.get("success") is False:
            print(f"Failed to delete incident {incident_id}")

        return result

    except Exception as e:
        print(f"Error in delete_incident: {e}")
        return {"success": False}


async def get_incident(incident_id: str, select_columns: List[str] = None, expand: str = None) -> Dict:
    try:
        endpoint_path = f"incidents({incident_id})"
        params = []

        if select_columns:
            params.append(f"$select={','.join(select_columns)}")
        if expand:
            params.append(f"$expand={expand}")
        if params:
            endpoint_path += "?" + "&".join(params)

        print(f"Retrieving incident: {incident_id}")
        result = await general_dataverse_webapi_request("GET", endpoint_path)

        if result.get("success") is False:
            print(f"Failed to retrieve incident {incident_id}")

        return result

    except Exception as e:
        print(f"Error in get_incident: {e}")
        return {"success": False}


async def query_incidents(filter_clause: str, select_columns: List[str] = None, expand: str = None, top: int = None, orderby: str = None) -> List[Dict]:
    try:
        params = [f"$filter={filter_clause}"]

        if select_columns:
            params.append(f"$select={','.join(select_columns)}")
        if expand:
            params.append(f"$expand={expand}")
        if top:
            params.append(f"$top={top}")
        if orderby:
            params.append(f"$orderby={orderby}")

        endpoint_path = "incidents?" + "&".join(params)

        print(f"Querying incidents: {filter_clause}")
        result = await general_dataverse_webapi_request("GET", endpoint_path)

        if result.get("success") is False:
            print("Failed to query incidents")
            return []

        return result.get("value", [])

    except Exception as e:
        print(f"Error in query_incidents: {e}")
        return []


async def get_validation_request_from_note(incident_id: str, note_title: str) -> Optional[Dict[str, Any]]:
    try:
        filter_clause = f"_objectid_value eq {incident_id} and subject eq '{note_title}'"

        endpoint_path = (
            f"annotations"
            f"?$select=notetext"
            f"&$filter={filter_clause}"
        )

        print(f"Searching for annotation with objectid={incident_id} and subject='{note_title}'")
        result = await general_dataverse_webapi_request("GET", endpoint_path)

        if result.get("success") is False:
            print("No result returned from Dataverse")
            return None

        records = result.get("value", [])
        if not records:
            print(f"No annotation found matching the criteria")
            return None

        note_desc = records[0].get("notetext", "")
        print(f"Retrieved notetext (first 200 chars): {note_desc[:200]}...")

        note_desc = note_desc.replace("Validation Request to Validation Services API:", "")
        note_desc = re.sub(r'\{"sectionStart"(\n|.)+"NoteSpecialDirectives"\}$', "", note_desc)

        note_desc = note_desc.strip()
        print(f"Cleaned notetext (first 200 chars): {note_desc[:200]}...")

        validation_request = json.loads(note_desc)
        print(json.dumps(validation_request, indent=4))
        return validation_request

    except Exception as e:
        print(f"Error in get_validation_request_from_note: {e}")
        return None


async def create_annotation(incident_id: str, subject: str, notetext: str, **kwargs) -> Dict:
    try:
        annotation = {
            "objectid_incident@odata.bind": f"/incidents({incident_id})",
            "subject": subject,
            "notetext": notetext,
        }
        annotation.update(kwargs)

        endpoint_path = "annotations"
        additional_headers = {}

        select_columns = ["annotationid", "subject", "notetext"]
        endpoint_path += "?$select=" + ",".join(select_columns)
        additional_headers["Prefer"] = "return=representation"

        print(f"Creating annotation for incident {incident_id} with subject '{subject}'")
        result = await general_dataverse_webapi_request("POST", endpoint_path, annotation, additional_headers)

        if result.get("success") is False:
            print("Failed to create annotation")

        return result

    except Exception as e:
        print(f"Error in create_annotation: {e}")
        return {"success": False}


async def update_annotation(annotation_id: str, fields: Dict[str, Any]) -> Dict:
    try:
        endpoint_path = f"annotations({annotation_id})"

        print(f"Updating annotation: {annotation_id}")
        result = await general_dataverse_webapi_request("PATCH", endpoint_path, fields)

        if result.get("success") is False:
            print(f"Failed to update annotation {annotation_id}")

        return result

    except Exception as e:
        print(f"Error in update_annotation: {e}")
        return {"success": False}


async def get_annotations(incident_id: str, subject: str = None, select_columns: List[str] = None) -> List[Dict]:
    try:
        filter_parts = [f"_objectid_value eq {incident_id}"]
        if subject:
            filter_parts.append(f"subject eq '{subject}'")
        filter_clause = " and ".join(filter_parts)

        if not select_columns:
            select_columns = ["annotationid", "subject", "notetext", "createdon"]

        endpoint_path = (
            f"annotations"
            f"?$select={','.join(select_columns)}"
            f"&$filter={filter_clause}"
        )

        print(f"Retrieving annotations for incident {incident_id}")
        result = await general_dataverse_webapi_request("GET", endpoint_path)

        if result.get("success") is False:
            print("Failed to retrieve annotations")
            return []

        return result.get("value", [])

    except Exception as e:
        print(f"Error in get_annotations: {e}")
        return []


async def retrieve_file_info(attachment_id: str) -> Optional[Dict[str, Any]]:
    try:
        endpoint_path = (
            f"msdyn_entityattachments({attachment_id})"
            f"?$select=msdyn_entityattachmentid"
            f"&$expand=msdyn_entityattachment_FileAttachments("
            f"$select=createdon,mimetype,filesizeinbytes,filename,regardingfieldname,fileattachmentid)"
        )

        print(f"Retrieving file info for attachment: {attachment_id}")
        result = await general_dataverse_webapi_request("GET", endpoint_path)

        if result.get("success") is False:
            print(f"No result returned for attachment {attachment_id}")
            return None

        file_attachments = result.get("msdyn_entityattachment_FileAttachments", [])
        if not file_attachments:
            print(f"No file attachments found for {attachment_id}")
            return None

        file_info = file_attachments[0]
        print(f"File info: filename={file_info.get('filename')}, "
              f"mimetype={file_info.get('mimetype')}, "
              f"size={file_info.get('filesizeinbytes')}, "
              f"createdon={file_info.get('createdon')}")
        return file_info

    except Exception as e:
        print(f"Error in retrieve_file_info: {e}")
        return None


async def download_validation_request_documents(incident_id: str, file_path: str) -> List[Dict[str, Any]]:
    try:
        filter_clause = f"_msdyn_relatedentity_value eq {incident_id}"

        endpoint_path = (
            f"msdyn_entityattachments"
            f"?$select=msdyn_entityattachmentid,ts_referencevalue,msdyn_name"
            f"&$filter={filter_clause}"
        )

        print(f"Searching for entity attachments for incident: {incident_id}")
        result = await general_dataverse_webapi_request("GET", endpoint_path)

        if result.get("success") is False:
            print("No result returned from Dataverse")
            return []

        records = result.get("value", [])
        if not records:
            print(f"No attachments found for incident {incident_id}")
            return []

        os.makedirs(file_path, exist_ok=True)

        token = await get_cached_token()
        if not token:
            print("Failed to obtain access token for file download")
            return []

        attachment_details = []

        for record in records:
            attachment_id = record.get("msdyn_entityattachmentid")
            file_name = record.get("msdyn_name", f"{attachment_id}.bin")
            regulatory_body = record.get("ts_referencevalue")

            download_url = f"{DYNAMICS_ENVIRONMENT}/api/data/v9.2/msdyn_entityattachments({attachment_id})/msdyn_fileblob/$value"

            file_info = await retrieve_file_info(attachment_id)

            content_type = file_info.get("mimetype") if file_info else None
            file_size_in_bytes = file_info.get("filesizeinbytes") if file_info else None
            created_on = file_info.get("createdon") if file_info else None

            print(f"Downloading attachment: {file_name} ({attachment_id}); regulatory body: {regulatory_body}")

            try:
                async with httpx.AsyncClient() as client:
                    response = await client.get(
                        download_url,
                        headers={"Authorization": f"Bearer {token}"},
                    )

                    if not response.is_success:
                        print(f"Failed to download {file_name}: HTTP {response.status_code}")
                        continue

                    full_path = os.path.join(file_path, file_name)
                    with open(full_path, "wb") as f:
                        f.write(response.content)

                    print(f"Saved: {full_path}")

                    attachment_details.append({
                        "attachmentId": attachment_id,
                        "regulatoryBody": regulatory_body,
                        "downloadUrl": download_url,
                        "fileName": file_name,
                        "contentType": content_type,
                        "fileSizeInBytes": file_size_in_bytes,
                        "createdOn": created_on,
                    })

            except Exception as e:
                print(f"Error downloading {file_name}: {e}")

        print(f"Downloaded {len(attachment_details)} of {len(records)} attachments")
        print(json.dumps(attachment_details, indent=4))

        return attachment_details

    except Exception as e:
        print(f"Error in download_validation_request_documents: {e}")
        return []


async def get_incident_account(incident_id: str, select_columns: List[str] = None) -> Optional[Dict[str, Any]]:
    try:
        if not select_columns:
            select_columns = [
                "accountid", "name", "address1_line1", "address1_city",
                "address1_stateorprovince", "address1_postalcode", "address1_country",
                "telephone1", "emailaddress1", "websiteurl",
            ]

        endpoint_path = (
            f"incidents({incident_id})"
            f"?$select=_customerid_value"
            f"&$expand=customerid_account($select={','.join(select_columns)})"
        )

        print(f"Retrieving account for incident {incident_id}")
        result = await general_dataverse_webapi_request("GET", endpoint_path)

        if result.get("success") is False:
            print(f"Failed to retrieve account for incident {incident_id}")
            return None

        account = result.get("customerid_account")
        if not account:
            print(f"No account linked to incident {incident_id}")
            return None

        print(f"Account: {account.get('name')} ({account.get('accountid')})")
        return account

    except Exception as e:
        print(f"Error in get_incident_account: {e}")
        return None


async def create_entity_attachment(incident_id: str, file_name: str, **kwargs) -> Optional[str]:
    try:
        attachment = {
            "msdyn_RelatedEntity_incident@odata.bind": f"/incidents({incident_id})",
            "msdyn_name": file_name,
        }
        attachment.update(kwargs)

        endpoint_path = "msdyn_entityattachments"

        print(f"Creating entity attachment '{file_name}' for incident {incident_id}")
        result = await general_dataverse_webapi_request("POST", endpoint_path, attachment)

        if result.get("success") is False:
            print("Failed to create entity attachment")
            return None

        attachment_id = result.get("msdyn_entityattachmentid") or result.get("entityId")
        print(f"Created entity attachment: {attachment_id}")
        return attachment_id

    except Exception as e:
        print(f"Error in create_entity_attachment: {e}")
        return None


async def upload_attachment_file(attachment_id: str, file_bytes: bytes, file_name: str, file_size: int) -> Dict:
    token = await get_cached_token()
    if not token:
        print("Failed to obtain access token")
        return {"success": False}

    url = f"{DYNAMICS_ENVIRONMENT}/api/data/v9.2/msdyn_entityattachments({attachment_id})/msdyn_fileblob"

    headers = {
        "OData-MaxVersion": "4.0",
        "OData-Version": "4.0",
        "Accept": "application/json",
        "Authorization": f"Bearer {token}",
        "x-ms-file-name": file_name,
        "Content-Type": "application/octet-stream",
        "Content-Length": str(file_size),
    }

    try:
        async with httpx.AsyncClient() as client:
            response = await client.patch(url, headers=headers, content=file_bytes)

        if not response.is_success:
            print(f"Failed to upload file: HTTP {response.status_code}: {response.text}")
            return {"success": False, "status": response.status_code}

        print(f"Successfully uploaded '{file_name}' to attachment {attachment_id}")
        return {"success": True, "status": response.status_code}

    except Exception as e:
        print(f"Error in upload_attachment_file: {e}")
        return {"success": False}


async def upload_incident_attachment(incident_id: str, file_name: str, file_bytes: bytes, file_size: int, **kwargs) -> Dict:
    try:
        attachment_id = await create_entity_attachment(incident_id, file_name, **kwargs)
        if not attachment_id:
            return {"success": False}

        result = await upload_attachment_file(attachment_id, file_bytes, file_name, file_size)

        if result.get("success") is not False:
            result["attachmentId"] = attachment_id

        return result

    except Exception as e:
        print(f"Error in upload_incident_attachment: {e}")
        return {"success": False}


async def resolve_incident(incident_id: str, subject: str, description: str = "", billable_time: int = 0) -> Dict:
    try:
        resolve_payload = {
            "IncidentResolution": {
                "subject": subject,
                "description": description,
                "incidentid@odata.bind": f"/incidents({incident_id})",
                "timespent": billable_time,
            },
            "Status": -1,
        }

        endpoint_path = "CloseIncident"

        print(f"Resolving incident {incident_id}...")
        result = await general_dataverse_webapi_request("POST", endpoint_path, resolve_payload)

        if result.get("success") is False:
            print(f"Failed to resolve incident {incident_id}")

        return result

    except Exception as e:
        print(f"Error in resolve_incident: {e}")
        return {"success": False}


async def query_entity(entity_set_name: str, filter_clause: str, select_columns: List[str] = None,
                       expand: str = None, top: int = None, orderby: str = None) -> List[Dict]:
    try:
        params = [f"$filter={filter_clause}"]
        if select_columns:
            params.append(f"$select={','.join(select_columns)}")
        if expand:
            params.append(f"$expand={expand}")
        if top:
            params.append(f"$top={top}")
        if orderby:
            params.append(f"$orderby={orderby}")

        endpoint_path = f"{entity_set_name}?" + "&".join(params)
        result = await general_dataverse_webapi_request("GET", endpoint_path)

        if result.get("success") is False:
            return []

        return result.get("value", [])
    except Exception as e:
        print(f"Error in query_entity: {e}")
        return []


async def get_entity(entity_set_name: str, entity_id: str, select_columns: List[str] = None,
                     expand: str = None) -> Optional[Dict]:
    try:
        endpoint_path = f"{entity_set_name}({entity_id})"
        params = []
        if select_columns:
            params.append(f"$select={','.join(select_columns)}")
        if expand:
            params.append(f"$expand={expand}")
        if params:
            endpoint_path += "?" + "&".join(params)

        result = await general_dataverse_webapi_request("GET", endpoint_path)
        if result.get("success") is False:
            return None
        return result
    except Exception as e:
        print(f"Error in get_entity: {e}")
        return None


async def create_entity(entity_set_name: str, fields: Dict[str, Any],
                        select_columns: List[str] = None) -> Dict:
    try:
        endpoint_path = entity_set_name
        additional_headers = {}
        if select_columns:
            endpoint_path += "?$select=" + ",".join(select_columns)
            additional_headers["Prefer"] = "return=representation"

        result = await general_dataverse_webapi_request("POST", endpoint_path, fields, additional_headers)
        if result.get("success") is False:
            print(f"Failed to create {entity_set_name} record")
        return result
    except Exception as e:
        print(f"Error in create_entity: {e}")
        return {"success": False}


async def update_entity(entity_set_name: str, entity_id: str, fields: Dict[str, Any],
                        select_columns: List[str] = None) -> Dict:
    try:
        endpoint_path = f"{entity_set_name}({entity_id})"
        additional_headers = {}
        if select_columns:
            endpoint_path += "?$select=" + ",".join(select_columns)
            additional_headers["Prefer"] = "return=representation"

        result = await general_dataverse_webapi_request("PATCH", endpoint_path, fields, additional_headers)
        if result.get("success") is False:
            print(f"Failed to update {entity_set_name}({entity_id})")
        return result
    except Exception as e:
        print(f"Error in update_entity: {e}")
        return {"success": False}


async def delete_entity(entity_set_name: str, entity_id: str) -> Dict:
    try:
        endpoint_path = f"{entity_set_name}({entity_id})"
        result = await general_dataverse_webapi_request("DELETE", endpoint_path)
        if result.get("success") is False:
            print(f"Failed to delete {entity_set_name}({entity_id})")
        return result
    except Exception as e:
        print(f"Error in delete_entity: {e}")
        return {"success": False}


async def get_option_set_value(option_set_name: str, label: str) -> Optional[int]:
    try:
        endpoint_path = f"GlobalOptionSetDefinitions(Name='{option_set_name}')"
        result = await general_dataverse_webapi_request("GET", endpoint_path)

        if result.get("success") is False:
            return None

        options = result.get("Options", [])
        for option in options:
            option_label = option.get("Label", {}).get("UserLocalizedLabel", {}).get("Label", "")
            if option_label.lower() == label.lower():
                return option.get("Value")

        return None
    except Exception as e:
        print(f"Error in get_option_set_value: {e}")
        return None


async def get_field_mapping_value(field_name: str, value: str,
                                  parent_field_value: str = None) -> Optional[Dict]:
    try:
        filter_parts = [
            f"ts_fieldname eq '{field_name}'",
            f"ts_value eq '{value}'",
        ]
        if parent_field_value:
            filter_parts.append(f"ts_parentfieldvalue eq '{parent_field_value}'")

        records = await query_entity(
            "ts_fieldhierarchyandmappings",
            " and ".join(filter_parts),
            select_columns=["ts_valuecode", "ts_value", "ts_name", "ts_mappedfieldvalue",
                            "ts_parentfieldvalue", "ts_configuration"],
        )

        if records:
            return records[0]
        return None
    except Exception as e:
        print(f"Error in get_field_mapping_value: {e}")
        return None


async def make_http_get_call(base_url: str, endpoint_path: str,
                             query_params: Dict[str, str] = None,
                             max_retries: int = 3) -> Optional[Dict]:
    for attempt in range(max_retries + 1):
        try:
            if attempt > 0:
                await asyncio.sleep(15)

            query_string = ""
            if query_params:
                from urllib.parse import urlencode
                query_string = "?" + urlencode(query_params)

            url = base_url + endpoint_path + query_string

            async with httpx.AsyncClient() as client:
                response = await client.get(url, headers={"Accept": "application/json"})

            if response.status_code != 200:
                print(f"make_http_get_call() returned status {response.status_code} (attempt {attempt + 1}/{max_retries + 1})")
                if attempt == max_retries:
                    return None
                continue

            return response.json()
        except Exception as e:
            print(f"Error in make_http_get_call (attempt {attempt + 1}/{max_retries + 1}): {e}")
            if attempt == max_retries:
                return None
    return None


async def make_http_post_call(request_body, base_url: str, endpoint_path: str,
                              query_params: Dict[str, str] = None,
                              extra_headers: Dict[str, str] = None,
                              max_retries: int = 3) -> Optional[Dict]:
    for attempt in range(max_retries + 1):
        try:
            if attempt > 0:
                await asyncio.sleep(15)

            query_string = ""
            if query_params:
                from urllib.parse import urlencode
                query_string = "?" + urlencode(query_params)

            url = base_url + endpoint_path + query_string

            headers = {"Accept": "application/json", "Content-Type": "application/json"}
            if extra_headers:
                headers.update(extra_headers)

            content = request_body if isinstance(request_body, str) else json.dumps(request_body)

            async with httpx.AsyncClient() as client:
                response = await client.post(url, headers=headers, content=content)

            if response.status_code != 200:
                print(f"make_http_post_call() returned status {response.status_code} (attempt {attempt + 1}/{max_retries + 1})")
                if attempt == max_retries:
                    return None
                continue

            return response.json()
        except Exception as e:
            print(f"Error in make_http_post_call (attempt {attempt + 1}/{max_retries + 1}): {e}")
            if attempt == max_retries:
                return None
    return None


def remove_hex_characters(text: str) -> str:
    if not text:
        return text
    return re.sub(r'[\x00-\x08\x0b\x0c\x0e-\x1f]', '', text)


def to_proper_case(text: str) -> str:
    if not text:
        return text
    return " ".join(word.capitalize() for word in text.split())


async def create_dataverse_entity(entity_name: str) -> Optional[Dict[str, Any]]:
    try:
        entity_logical_name = "ts_" + entity_name.lower()
        solution_unique_name = "TSDataArchitecture"
        # "AIrtDataverseIntegration"
        language_code = 1033

        entity_definition = {
            "@odata.type": "Microsoft.Dynamics.CRM.EntityMetadata",
            "SchemaName": entity_logical_name,
            "DisplayName": {
                "@odata.type": "Microsoft.Dynamics.CRM.Label",
                "LocalizedLabels": [
                    {
                        "@odata.type": "Microsoft.Dynamics.CRM.LocalizedLabel",
                        "Label": entity_name,
                        "LanguageCode": language_code,
                    }
                ],
            },
            "DisplayCollectionName": {
                "@odata.type": "Microsoft.Dynamics.CRM.Label",
                "LocalizedLabels": [
                    {
                        "@odata.type": "Microsoft.Dynamics.CRM.LocalizedLabel",
                        "Label": entity_name,
                        "LanguageCode": language_code,
                    }
                ],
            },
            "OwnershipType": "UserOwned",
            "IsActivity": False,
            "HasNotes": False,
            "HasActivities": False,
            "PrimaryNameAttribute": entity_logical_name + "_name",
            "Attributes": [
                {
                    "@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata",
                    "AttributeType": "String",
                    "SchemaName": entity_logical_name + "_name",
                    "DisplayName": {
                        "@odata.type": "Microsoft.Dynamics.CRM.Label",
                        "LocalizedLabels": [
                            {
                                "@odata.type": "Microsoft.Dynamics.CRM.LocalizedLabel",
                                "Label": "Name",
                                "LanguageCode": language_code,
                            }
                        ],
                    },
                    "RequiredLevel": {
                        "Value": "ApplicationRequired",
                        "CanBeChanged": True,
                    },
                    "MaxLength": 100,
                }
            ],
        }

        endpoint_path = f"EntityDefinitions?solutionUniqueName={solution_unique_name}"

        print(f"Creating Dataverse entity '{entity_logical_name}'...")
        result = await general_dataverse_webapi_request("POST", endpoint_path, entity_definition)

        if result.get("success") is not False:
            entity_id = result.get("MetadataId") or result.get("entityId")
            print(f"Successfully created entity '{entity_logical_name}' with Id: {entity_id}")
        else:
            print(f"Failed to create entity '{entity_logical_name}'")

        return result

    except Exception as e:
        print(f"Error in create_dataverse_entity: {e}")
        return None


async def create_string_attribute(entity_logical_name: str, attribute_name: str, string_length: int) -> Optional[Dict[str, Any]]:
    try:
        attribute_logical_name = "ts_" + attribute_name.lower()
        solution_unique_name = "TSDataArchitecture"
        # "AIrtDataverseIntegration"
        language_code = 1033

        attribute_definition = {
            "@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata",
            "AttributeType": "String",
            "SchemaName": attribute_logical_name,
            "DisplayName": {
                "@odata.type": "Microsoft.Dynamics.CRM.Label",
                "LocalizedLabels": [
                    {
                        "@odata.type": "Microsoft.Dynamics.CRM.LocalizedLabel",
                        "Label": attribute_name,
                        "LanguageCode": language_code,
                    }
                ],
            },
            "RequiredLevel": {
                "Value": "None",
                "CanBeChanged": True,
            },
            "MaxLength": string_length,
        }

        endpoint_path = f"EntityDefinitions(LogicalName='{entity_logical_name}')/Attributes?solutionUniqueName={solution_unique_name}"

        print(f"Creating string attribute '{attribute_logical_name}' on entity '{entity_logical_name}' (max length: {string_length})...")
        result = await general_dataverse_webapi_request("POST", endpoint_path, attribute_definition)

        if result.get("success") is not False:
            metadata_id = result.get("MetadataId") or result.get("entityId")
            print(f"Successfully created attribute '{attribute_logical_name}' with Id: {metadata_id}")
        else:
            print(f"Failed to create attribute '{attribute_logical_name}'")

        return result

    except Exception as e:
        print(f"Error in create_string_attribute: {e}")
        return None


async def create_numeric_attribute(entity_logical_name: str, attribute_name: str, data_type: str, size: Optional[int] = None) -> Optional[Dict[str, Any]]:
    try:
        attribute_logical_name = "ts_" + attribute_name.lower()
        solution_unique_name = "TSDataArchitecture"
        # "AIrtDataverseIntegration"
        language_code = 1033

        type_map = {
            "BigIntType": ("Microsoft.Dynamics.CRM.BigIntAttributeMetadata", "BigInt"),
            "DecimalType": ("Microsoft.Dynamics.CRM.DecimalAttributeMetadata", "Decimal"),
            "IntegerType": ("Microsoft.Dynamics.CRM.IntegerAttributeMetadata", "Integer"),
            "DoubleType": ("Microsoft.Dynamics.CRM.DoubleAttributeMetadata", "Double"),
        }

        max_defaults = {
            "BigIntType": 9223372036854775807,
            "IntegerType": 2147483647,
            "DecimalType": 10,
            "DoubleType": 5,
        }

        if data_type not in type_map:
            print(f"Unsupported numeric data type: {data_type}. Must be one of: {', '.join(type_map.keys())}")
            return None

        odata_type, attribute_type = type_map[data_type]

        effective_size = size if size is not None else max_defaults[data_type]

        attribute_definition = {
            "@odata.type": odata_type,
            "AttributeType": attribute_type,
            "SchemaName": attribute_logical_name,
            "DisplayName": {
                "@odata.type": "Microsoft.Dynamics.CRM.Label",
                "LocalizedLabels": [
                    {
                        "@odata.type": "Microsoft.Dynamics.CRM.LocalizedLabel",
                        "Label": attribute_name,
                        "LanguageCode": language_code,
                    }
                ],
            },
            "RequiredLevel": {
                "Value": "None",
                "CanBeChanged": True,
            },
        }

        if data_type == "IntegerType":
            attribute_definition["MaxValue"] = effective_size
            attribute_definition["MinValue"] = 0
        elif data_type == "BigIntType":
            attribute_definition["MaxValue"] = effective_size
            attribute_definition["MinValue"] = 0
        elif data_type == "DecimalType":
            attribute_definition["Precision"] = effective_size
        elif data_type == "DoubleType":
            attribute_definition["Precision"] = effective_size

        endpoint_path = f"EntityDefinitions(LogicalName='{entity_logical_name}')/Attributes?solutionUniqueName={solution_unique_name}"

        print(f"Creating {data_type} attribute '{attribute_logical_name}' on entity '{entity_logical_name}' (size: {effective_size})...")
        result = await general_dataverse_webapi_request("POST", endpoint_path, attribute_definition)

        if result.get("success") is not False:
            metadata_id = result.get("MetadataId") or result.get("entityId")
            print(f"Successfully created attribute '{attribute_logical_name}' with Id: {metadata_id}")
        else:
            print(f"Failed to create attribute '{attribute_logical_name}'")

        return result

    except Exception as e:
        print(f"Error in create_numeric_attribute: {e}")
        return None


async def main():
    try:
        transaction_id = "TechSoup_2801361_20260313"

        print(f"Starting validation request processing for transaction: {transaction_id}")
        print("-" * 60)

        case_record = await find_case_by_transaction_id(transaction_id)

        if case_record:
            incident_id = case_record.get("incidentid")
            note_title = f"ValidationRequest:{transaction_id}"
            validation_request = await get_validation_request_from_note(incident_id, note_title)

            downloaded_files = await download_validation_request_documents(
                incident_id, r"C:\ValidationRequestArtifacts"
            )

            
            print("Successfully retrieved incident record")
            print(f"transactionId: {transaction_id}")
            print(f"caseId: {case_record.get('incidentid')}")
            print(f"Legal Name: {case_record.get('ts_validationrequestlegalname')}")
            print(f"Address Line 1: {case_record.get('ts_validationrequestaddressline1')}")
            print(f"City: {case_record.get('ts_validationrequestaddresscity')}")
            print(f"State/Region: {case_record.get('ts_validationrequestaddressstateregion')}")
            print(f"Postal Code: {case_record.get('ts_validationrequestaddresspostalcode')}")
            print(f"Country ID: {case_record.get('ts_validationrequestaddresscountryid')}")
            print(f"Email: {case_record.get('ts_validationrequestemail')}")
            print(f"Phone: {case_record.get('ts_validationrequestphone')}")
            print(f"Website: {case_record.get('ts_validationrequestwebsite')}")
            print(f"Org Type: {case_record.get('ts_validationrequestorgtype')}")
            print(f"Legal Identifier: {case_record.get('ts_validationrequestlegalidentifier')}")

            account_data = case_record.get("customerid_account")
            if account_data:
                print(f"Client that submitted the Validation Request: {account_data.get('name')}")
            else:
                print(f"Customer Id (lookup value): {case_record.get('_customerid_value')}")
        else:
            print("Failed to retrieve incident record")

    except Exception as e:
        print(f"Error in main: {e}")


if __name__ == "__main__":
    asyncio.run(main())
