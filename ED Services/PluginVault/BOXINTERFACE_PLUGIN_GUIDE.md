# BoxInterface Plugin for Dynamics 365

## Overview

The BoxInterface plugin serves as a Custom API wrapper for the BoxApiService, enabling Dynamics 365 to interact with Box.com through a standardized interface. This plugin handles the conversion between Dynamics 365 expando objects (with @odata.type elements) and clean JSON for Box API calls.

## Architecture

```
Dynamics 365 Custom API Call
    ↓
BoxInterface Plugin
    ↓
Input: Entity with @odata.type elements
    ↓
Convert to Clean JSON (strip @odata.type)
    ↓
Call Embedded BoxApiService
    ↓ 
Process Box API Response
    ↓
Convert JSON Response to Entity (add @odata.type)
    ↓
Return: Entity with @odata.type elements
    ↓
Dynamics 365 receives structured response
```

## Key Components

### 1. BoxInterface Plugin
- **Type**: Dynamics 365 Custom API Plugin
- **Input**: Generic Entity type with @odata.type elements
- **Output**: Generic Entity type with @odata.type elements
- **Authentication**: Hardcoded BoxAccessToken

### 2. JSON Conversion
- **ConvertExpandoToJson**: Removes @odata.type elements from input
- **ConvertJsonToExpando**: Adds @odata.type elements to response
- **Translated from Python**: Based on techsoupservices_helper.py functions

### 3. Embedded BoxApiService
- **Simplified Implementation**: Core Box API functionality embedded in plugin
- **Supports**: Folder analysis, file retrieval, file upload
- **Authentication**: Uses hardcoded access token

## Usage Examples

### Input Format (with @odata.type)
```json
{
  "@odata.type": "#Microsoft.Dynamics.CRM.expando",
  "itemType": "Folder",
  "itemId": "123456789",
  "fileOperation": "retrieval"
}
```

### Converted to Clean JSON (for Box API)
```json
{
  "itemType": "Folder", 
  "itemId": "123456789",
  "fileOperation": "retrieval"
}
```

### Box API Response (Clean JSON)
```json
{
  "success": true,
  "message": "Operation completed successfully",
  "folderId": "123456789",
  "folderContents": [
    {
      "id": "987654321",
      "name": "Sample Document.pdf",
      "type": "file",
      "size": 1024000
    }
  ]
}
```

### Converted Output (with @odata.type)
```json
{
  "@odata.type": "#Microsoft.Dynamics.CRM.expando",
  "success": true,
  "message": "Operation completed successfully", 
  "folderId": "123456789",
  "folderContents@odata.type": "#Collection(Microsoft.Dynamics.CRM.expando)",
  "folderContents": [
    {
      "@odata.type": "#Microsoft.Dynamics.CRM.expando",
      "id": "987654321",
      "name": "Sample Document.pdf",
      "type": "file",
      "size": 1024000
    }
  ]
}
```

## Supported Operations

### 1. Folder Analysis
```json
{
  "@odata.type": "#Microsoft.Dynamics.CRM.expando",
  "itemType": "Folder",
  "itemId": "folder_id"
}
```

### 2. File Retrieval
```json
{
  "@odata.type": "#Microsoft.Dynamics.CRM.expando", 
  "itemType": "File",
  "itemId": "file_id",
  "fileOperation": "retrieval"
}
```

### 3. File Upload
```json
{
  "@odata.type": "#Microsoft.Dynamics.CRM.expando",
  "itemType": "File",
  "fileOperation": "upload",
  "fileFullPath": "C:\\path\\to\\file.txt",
  "folderId": "destination_folder_id"
}
```

## Implementation Details

### Authentication
- **Hardcoded Token**: BoxAccessToken = "REDACTED"
- **No OAuth Flow**: Skips interactive authentication
- **Direct API Access**: Uses Bearer token for all Box API calls

### Error Handling
- **Comprehensive Logging**: All operations logged to Dynamics tracing service
- **Exception Wrapping**: Dynamics-compatible exception handling
- **Graceful Degradation**: Returns error information in response structure

### Data Type Conversion
- **Uniform Object Arrays**: Detected and properly annotated
- **Nested Objects**: Recursive conversion with proper @odata.type elements
- **Collection Types**: Proper #Collection annotations for arrays
- **Primitive Types**: Direct value assignment

## Configuration

### Custom API Setup in Dynamics 365

1. **Create Custom API**
   - Name: `ts_BoxInterface`
   - Unique Name: `ts_BoxInterface`
   - Display Name: `Box Interface`

2. **Input Parameter**
   - Name: `input`
   - Type: `Entity`
   - Description: `Input data with @odata.type elements`

3. **Output Parameter**
   - Name: `output`
   - Type: `Entity`
   - Description: `Output data with @odata.type elements`

4. **Plugin Registration**
   - Assembly: `EDServices.dll`
   - Plugin Type: `EDServices.BoxInterface`
   - Step: Custom API execution

### Deployment Steps

1. **Build Plugin Assembly**
   ```bash
   dotnet build EDServices.csproj
   ```

2. **Register Assembly in Dynamics 365**
   - Upload `EDServices.dll` to Plugin Registration Tool
   - Register BoxInterface plugin type

3. **Create Custom API Step**
   - Associate plugin with Custom API
   - Set execution stage to synchronous

4. **Test Integration**
   ```javascript
   // From Dynamics 365 JavaScript
   var request = {
       input: {
           "@odata.type": "#Microsoft.Dynamics.CRM.expando",
           "itemType": "Folder",
           "itemId": "0"
       }
   };
   
   Xrm.WebApi.online.execute("ts_BoxInterface", request)
       .then(function(result) {
           console.log(result.output);
       });
   ```

## Benefits

### 1. Seamless Integration
- **Native Dynamics Feel**: Uses standard Dynamics entity types
- **No External Dependencies**: Self-contained plugin assembly
- **Standard Error Handling**: Follows Dynamics patterns

### 2. Developer Experience
- **Familiar JSON**: Clean JSON for business logic
- **Automatic Conversion**: No manual @odata.type management
- **Comprehensive Logging**: Full traceability of operations

### 3. Maintainability
- **Single Codebase**: All Box functionality in one plugin
- **Version Control**: Standard Dynamics plugin deployment
- **Configuration Management**: Centralized token management

## Security Considerations

### Token Management
- **Hardcoded Approach**: Simple but requires code updates for token refresh
- **Alternative Options**: Consider configuration entities for production
- **Audit Trail**: All operations logged with user context

### Access Control
- **Dynamics Security**: Inherits Dynamics 365 user permissions
- **Box Permissions**: Limited by Box access token permissions
- **Plugin Security**: Runs in sandbox mode

## Performance Optimization

### Caching Strategy
- **No Caching**: Simple stateless implementation
- **Future Enhancement**: Consider caching Box API responses
- **Memory Management**: Proper disposal of HTTP clients

### Async Operations
- **Synchronous Implementation**: Simplified for Custom API context
- **GetAwaiter().GetResult()**: Used for async-to-sync conversion
- **Timeout Handling**: HTTP client timeout configuration

This BoxInterface plugin provides a robust, maintainable bridge between Dynamics 365 and Box.com, handling all the complexities of data conversion and API integration while maintaining the familiar Dynamics development experience.
