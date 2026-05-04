// BoxInterface Plugin Test Examples
// For use in Dynamics 365 JavaScript or C# testing

// Example 1: Folder Analysis
var folderAnalysisRequest = {
    input: {
        "@odata.type": "#Microsoft.Dynamics.CRM.expando",
        "itemType": "Folder",
        "itemId": "0" // Root folder
    }
};

// Call the Custom API
Xrm.WebApi.online.execute("ts_BoxInterface", folderAnalysisRequest)
    .then(function(result) {
        console.log("Folder Analysis Result:", result.output);
        // Result will contain:
        // - success: true/false
        // - folderContents: Array of files and folders
        // - collaborators: Array of collaborators
        // - message: Success/error message
    })
    .catch(function(error) {
        console.error("Folder analysis failed:", error);
    });

// Example 2: File Retrieval
var fileRetrievalRequest = {
    input: {
        "@odata.type": "#Microsoft.Dynamics.CRM.expando",
        "itemType": "File",
        "itemId": "1963651881342", // File ID from previous folder analysis
        "fileOperation": "retrieval"
    }
};

Xrm.WebApi.online.execute("ts_BoxInterface", fileRetrievalRequest)
    .then(function(result) {
        console.log("File Retrieval Result:", result.output);
        // Result will contain:
        // - success: true/false
        // - folderContents: Array with single file details
        // - fileContent: Base64 encoded file content
        // - message: Success/error message
    })
    .catch(function(error) {
        console.error("File retrieval failed:", error);
    });

// Example 3: File Upload (Simulated in Plugin)
var fileUploadRequest = {
    input: {
        "@odata.type": "#Microsoft.Dynamics.CRM.expando",
        "itemType": "File",
        "fileOperation": "upload",
        "fileFullPath": "C:\\temp\\document.pdf", // Local file path
        "folderId": "0" // Destination folder ID
    }
};

Xrm.WebApi.online.execute("ts_BoxInterface", fileUploadRequest)
    .then(function(result) {
        console.log("File Upload Result:", result.output);
        // Result will contain:
        // - success: true/false
        // - itemId: New file ID in Box
        // - folderContents: Array with uploaded file details
        // - fileOperation: "upload"
        // - message: Success/error message
    })
    .catch(function(error) {
        console.error("File upload failed:", error);
    });

// Example 4: Error Handling Pattern
function callBoxInterface(requestData) {
    return Xrm.WebApi.online.execute("ts_BoxInterface", requestData)
        .then(function(result) {
            if (result.output && result.output.success) {
                return result.output;
            } else {
                throw new Error(result.output.message || "Unknown error occurred");
            }
        })
        .catch(function(error) {
            console.error("BoxInterface error:", error);
            throw error;
        });
}

// Example 5: Processing Folder Contents
function processFolderContents(folderResponse) {
    if (folderResponse.folderContents && folderResponse.folderContents.length > 0) {
        folderResponse.folderContents.forEach(function(item) {
            if (item.type === "file") {
                console.log("File:", item.name, "Size:", item.size, "bytes");
            } else if (item.type === "folder") {
                console.log("Folder:", item.name, "Items:", item.size);
            }
        });
    } else {
        console.log("No items found in folder");
    }
}

// Example 6: Chain Operations
function analyzeAndRetrieveFirstFile(folderId) {
    // First, analyze the folder
    var folderRequest = {
        input: {
            "@odata.type": "#Microsoft.Dynamics.CRM.expando",
            "itemType": "Folder",
            "itemId": folderId
        }
    };
    
    return callBoxInterface(folderRequest)
        .then(function(folderResult) {
            // Find the first file in the folder
            var files = folderResult.folderContents.filter(item => item.type === "file");
            if (files.length > 0) {
                // Retrieve the first file
                var fileRequest = {
                    input: {
                        "@odata.type": "#Microsoft.Dynamics.CRM.expando",
                        "itemType": "File",
                        "itemId": files[0].id,
                        "fileOperation": "retrieval"
                    }
                };
                return callBoxInterface(fileRequest);
            } else {
                throw new Error("No files found in folder");
            }
        })
        .then(function(fileResult) {
            console.log("Retrieved file content:", fileResult.folderContents[0].name);
            return fileResult;
        });
}

// C# Example for testing in Console Application or Unit Tests
/*
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Tooling.Connector;
using Newtonsoft.Json;

public class BoxInterfaceTest
{
    public void TestFolderAnalysis()
    {
        // Connect to Dynamics 365
        var connectionString = "AuthType=OAuth;Username=user@domain.com;Url=https://org.crm.dynamics.com;AppId=app-id;RedirectUri=app://redirect;";
        var service = new CrmServiceClient(connectionString);
        
        // Create input entity
        var inputEntity = new Entity("expando");
        inputEntity["@odata.type"] = "#Microsoft.Dynamics.CRM.expando";
        inputEntity["itemType"] = "Folder";
        inputEntity["itemId"] = "0";
        
        // Create request
        var request = new OrganizationRequest("ts_BoxInterface");
        request.Parameters["input"] = inputEntity;
        
        // Execute
        var response = service.Execute(request);
        var output = (Entity)response.Results["output"];
        
        // Process response
        if (output.GetAttributeValue<bool>("success"))
        {
            var folderContents = output.GetAttributeValue<EntityCollection>("folderContents");
            Console.WriteLine($"Found {folderContents.Entities.Count} items");
        }
        else
        {
            Console.WriteLine($"Error: {output.GetAttributeValue<string>("message")}");
        }
    }
}
*/
