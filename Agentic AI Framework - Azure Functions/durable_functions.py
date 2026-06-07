"""
Durable Functions for TechSoupServices
======================================

This module demonstrates Azure Durable Functions patterns for long-running,
stateful workflows that coordinate between Dynamics 365 CRM and NetSuite ERP.

Key Concepts:
-------------
1. ORCHESTRATOR: Coordinates the workflow, manages state, calls activities
2. ACTIVITY: Performs actual work (API calls, data processing)
3. CLIENT/STARTER: HTTP trigger that initiates orchestrations

Why Durable Functions vs Regular Functions?
-------------------------------------------
- Regular functions timeout after 5-10 minutes
- Regular functions lose state on failure
- Regular functions can't wait for external events efficiently
- Regular functions can't coordinate parallel work easily

Durable Functions solve all of these problems:
- Can run for days/weeks (state is checkpointed)
- Automatic retry with state preservation
- Can wait for external events without compute cost
- Built-in fan-out/fan-in pattern for parallel processing
"""

import azure.functions as func
import azure.durable_functions as df
import json
import logging
import requests
from typing import Dict, List, Any, Optional
from datetime import datetime, timedelta
import os


# =============================================================================
# DURABLE FUNCTIONS BLUEPRINT
# =============================================================================
# Note: In Python V2 model, we use a blueprint for durable functions

bp = df.Blueprint()


# =============================================================================
# EXAMPLE 1: MULTI-SYSTEM SYNC WORKFLOW (Function Chaining Pattern)
# =============================================================================
# This workflow syncs records from Dynamics to NetSuite in a reliable way:
# 1. Fetch records from Dynamics
# 2. Transform data format
# 3. Validate business rules
# 4. Send to NetSuite
# 5. Update Dynamics with sync status
#
# If any step fails, the workflow resumes from where it left off!


@bp.orchestration_trigger(context_name="context")
def sync_dynamics_to_netsuite_orchestrator(context: df.DurableOrchestrationContext):
    """
    Orchestrator: Coordinates the multi-system sync workflow.
    
    IMPORTANT: Orchestrator code must be DETERMINISTIC!
    - No I/O operations (no API calls, no file reads)
    - No random numbers or current time (use context.current_utc_datetime)
    - No async calls except to activities
    
    Why? The orchestrator replays from the beginning on each checkpoint.
    Non-deterministic code would produce different results on replay.
    """
    # Get input passed when starting the orchestration
    input_data = context.get_input()
    entity_name = input_data.get("entity_name", "accounts")
    batch_size = input_data.get("batch_size", 100)
    
    results = {
        "orchestration_id": context.instance_id,
        "started_at": str(context.current_utc_datetime),
        "entity_name": entity_name,
        "steps": []
    }
    
    try:
        # Step 1: Fetch records from Dynamics CRM
        # The yield keyword is crucial - it checkpoints state and waits for activity
        dynamics_records = yield context.call_activity(
            "fetch_dynamics_records",
            {"entity_name": entity_name, "batch_size": batch_size}
        )
        results["steps"].append({
            "step": "fetch_dynamics_records",
            "status": "completed",
            "record_count": len(dynamics_records) if dynamics_records else 0
        })
        
        if not dynamics_records:
            results["status"] = "completed_no_records"
            return results
        
        # Step 2: Transform data for NetSuite format
        transformed_records = yield context.call_activity(
            "transform_for_netsuite",
            {"records": dynamics_records}
        )
        results["steps"].append({
            "step": "transform_for_netsuite",
            "status": "completed",
            "record_count": len(transformed_records) if transformed_records else 0
        })
        
        # Step 3: Validate business rules
        validation_result = yield context.call_activity(
            "validate_sync_records",
            {"records": transformed_records}
        )
        results["steps"].append({
            "step": "validate_sync_records",
            "status": "completed",
            "valid_count": validation_result.get("valid_count", 0),
            "invalid_count": validation_result.get("invalid_count", 0)
        })
        
        valid_records = validation_result.get("valid_records", [])
        
        if not valid_records:
            results["status"] = "completed_no_valid_records"
            return results
        
        # Step 4: Send to NetSuite
        netsuite_result = yield context.call_activity(
            "send_to_netsuite",
            {"records": valid_records}
        )
        results["steps"].append({
            "step": "send_to_netsuite",
            "status": "completed",
            "success_count": netsuite_result.get("success_count", 0),
            "error_count": netsuite_result.get("error_count", 0)
        })
        
        # Step 5: Update Dynamics with sync status
        update_result = yield context.call_activity(
            "update_dynamics_sync_status",
            {
                "netsuite_results": netsuite_result,
                "entity_name": entity_name
            }
        )
        results["steps"].append({
            "step": "update_dynamics_sync_status",
            "status": "completed",
            "updated_count": update_result.get("updated_count", 0)
        })
        
        results["status"] = "completed_successfully"
        results["completed_at"] = str(context.current_utc_datetime)
        
    except Exception as e:
        results["status"] = "failed"
        results["error"] = str(e)
    
    return results


# =============================================================================
# EXAMPLE 2: PARALLEL BATCH PROCESSING (Fan-out/Fan-in Pattern)
# =============================================================================
# Process multiple records in parallel, then aggregate results.
# Much faster than sequential processing!


@bp.orchestration_trigger(context_name="context")
def parallel_batch_sync_orchestrator(context: df.DurableOrchestrationContext):
    """
    Orchestrator: Processes records in parallel batches.
    
    This demonstrates the Fan-out/Fan-in pattern:
    1. Fan-out: Spawn multiple activity functions in parallel
    2. Fan-in: Wait for all to complete, aggregate results
    
    This is MUCH faster than processing sequentially and handles
    failures per-record rather than failing the entire batch.
    """
    input_data = context.get_input()
    records = input_data.get("records", [])
    
    if not records:
        return {"status": "no_records", "processed": 0}
    
    # Fan-out: Create a task for each record (or batch of records)
    # These all run in PARALLEL!
    parallel_tasks = []
    for record in records:
        task = context.call_activity("process_single_record", record)
        parallel_tasks.append(task)
    
    # Fan-in: Wait for ALL tasks to complete
    # This is where the magic happens - orchestrator sleeps (free!)
    # until all parallel activities finish
    all_results = yield context.task_all(parallel_tasks)
    
    # Aggregate results
    success_count = sum(1 for r in all_results if r.get("success"))
    error_count = sum(1 for r in all_results if not r.get("success"))
    
    return {
        "status": "completed",
        "total_records": len(records),
        "success_count": success_count,
        "error_count": error_count,
        "results": all_results
    }


# =============================================================================
# EXAMPLE 3: APPROVAL WORKFLOW (Human Interaction Pattern)
# =============================================================================
# Wait for human approval before proceeding. Can wait for days/weeks!


@bp.orchestration_trigger(context_name="context")
def approval_workflow_orchestrator(context: df.DurableOrchestrationContext):
    """
    Orchestrator: Waits for human approval with timeout.
    
    This demonstrates waiting for external events:
    - Orchestrator sleeps (costs nothing) while waiting
    - External system can raise event to continue
    - Built-in timeout handling
    """
    input_data = context.get_input()
    request_id = input_data.get("request_id")
    approver_email = input_data.get("approver_email")
    timeout_hours = input_data.get("timeout_hours", 72)  # Default 3 days
    
    # Step 1: Send approval request notification
    yield context.call_activity(
        "send_approval_notification",
        {
            "request_id": request_id,
            "approver_email": approver_email,
            "orchestration_id": context.instance_id
        }
    )
    
    # Step 2: Wait for approval OR timeout
    # This is where Durable Functions shine - the orchestrator SLEEPS
    # No compute cost while waiting!
    
    # Create timeout deadline
    timeout_deadline = context.current_utc_datetime + timedelta(hours=timeout_hours)
    timeout_task = context.create_timer(timeout_deadline)
    
    # Wait for approval event (raised by external system via HTTP)
    approval_task = context.wait_for_external_event("ApprovalReceived")
    
    # Race: whichever happens first wins
    winner = yield context.task_any([approval_task, timeout_task])
    
    if winner == timeout_task:
        # Timeout occurred
        return {
            "status": "timeout",
            "request_id": request_id,
            "message": f"Approval not received within {timeout_hours} hours"
        }
    else:
        # Approval received - cancel the timer
        timeout_task.cancel()
        
        approval_data = approval_task.result
        is_approved = approval_data.get("approved", False)
        
        if is_approved:
            # Process the approved request
            result = yield context.call_activity(
                "process_approved_request",
                {"request_id": request_id, "approval_data": approval_data}
            )
            return {
                "status": "approved",
                "request_id": request_id,
                "result": result
            }
        else:
            # Handle rejection
            yield context.call_activity(
                "process_rejected_request",
                {"request_id": request_id, "rejection_reason": approval_data.get("reason")}
            )
            return {
                "status": "rejected",
                "request_id": request_id,
                "reason": approval_data.get("reason")
            }


# =============================================================================
# ACTIVITY FUNCTIONS
# =============================================================================
# These do the actual work. They CAN have I/O, API calls, etc.


@bp.activity_trigger(input_name="input")
def fetch_dynamics_records(input: Dict[str, Any]) -> List[Dict]:
    """
    Activity: Fetch records from Dynamics 365 CRM.
    
    Activities are where you do actual work:
    - API calls
    - Database operations
    - File I/O
    - Any non-deterministic operations
    """
    entity_name = input.get("entity_name", "accounts")
    batch_size = input.get("batch_size", 100)
    
    logging.info(f"Fetching {batch_size} {entity_name} from Dynamics...")
    
    # In real implementation, you would:
    # 1. Get access token using dynamics_helper.get_ms_token_web_api()
    # 2. Call Dynamics Web API
    # 3. Return the records
    
    # Simulated response for demonstration
    return [
        {"id": f"dyn-{i}", "name": f"Record {i}", "email": f"record{i}@example.com"}
        for i in range(min(batch_size, 10))
    ]


@bp.activity_trigger(input_name="input")
def transform_for_netsuite(input: Dict[str, Any]) -> List[Dict]:
    """
    Activity: Transform Dynamics records to NetSuite format.
    """
    records = input.get("records", [])
    
    logging.info(f"Transforming {len(records)} records for NetSuite...")
    
    transformed = []
    for record in records:
        transformed.append({
            "externalId": record.get("id"),
            "companyName": record.get("name"),
            "email": record.get("email"),
            "subsidiary": "1",  # Default subsidiary
            "source": "Dynamics365"
        })
    
    return transformed


@bp.activity_trigger(input_name="input")
def validate_sync_records(input: Dict[str, Any]) -> Dict[str, Any]:
    """
    Activity: Validate records against business rules.
    """
    records = input.get("records", [])
    
    logging.info(f"Validating {len(records)} records...")
    
    valid_records = []
    invalid_records = []
    
    for record in records:
        # Example validation rules
        if record.get("email") and record.get("companyName"):
            valid_records.append(record)
        else:
            invalid_records.append({
                "record": record,
                "error": "Missing required fields"
            })
    
    return {
        "valid_records": valid_records,
        "invalid_records": invalid_records,
        "valid_count": len(valid_records),
        "invalid_count": len(invalid_records)
    }


@bp.activity_trigger(input_name="input")
def send_to_netsuite(input: Dict[str, Any]) -> Dict[str, Any]:
    """
    Activity: Send records to NetSuite.
    """
    records = input.get("records", [])
    
    logging.info(f"Sending {len(records)} records to NetSuite...")
    
    # In real implementation, you would:
    # 1. Get NetSuite token using NetSuiteTokenGenerator
    # 2. Call NetSuite RESTlet
    # 3. Handle responses
    
    # Simulated response
    success_count = len(records)
    return {
        "success_count": success_count,
        "error_count": 0,
        "results": [{"id": r.get("externalId"), "status": "created"} for r in records]
    }


@bp.activity_trigger(input_name="input")
def update_dynamics_sync_status(input: Dict[str, Any]) -> Dict[str, Any]:
    """
    Activity: Update Dynamics records with sync status.
    """
    netsuite_results = input.get("netsuite_results", {})
    entity_name = input.get("entity_name")
    
    results = netsuite_results.get("results", [])
    logging.info(f"Updating {len(results)} {entity_name} in Dynamics with sync status...")
    
    # In real implementation, you would update Dynamics records
    
    return {
        "updated_count": len(results),
        "entity_name": entity_name
    }


@bp.activity_trigger(input_name="input")
def process_single_record(input: Dict[str, Any]) -> Dict[str, Any]:
    """
    Activity: Process a single record (for parallel processing example).
    """
    record_id = input.get("id", "unknown")
    
    logging.info(f"Processing record: {record_id}")
    
    # Simulate processing
    try:
        # Your actual processing logic here
        return {"id": record_id, "success": True}
    except Exception as e:
        return {"id": record_id, "success": False, "error": str(e)}


@bp.activity_trigger(input_name="input")
def send_approval_notification(input: Dict[str, Any]) -> Dict[str, Any]:
    """
    Activity: Send approval notification email.
    """
    request_id = input.get("request_id")
    approver_email = input.get("approver_email")
    orchestration_id = input.get("orchestration_id")
    
    logging.info(f"Sending approval request to {approver_email} for request {request_id}")
    
    # In real implementation, send email with approval link
    # The link would call the /approve endpoint with orchestration_id
    
    return {"sent": True, "request_id": request_id}


@bp.activity_trigger(input_name="input")
def process_approved_request(input: Dict[str, Any]) -> Dict[str, Any]:
    """
    Activity: Process an approved request.
    """
    request_id = input.get("request_id")
    logging.info(f"Processing approved request: {request_id}")
    
    # Your approval processing logic here
    
    return {"processed": True, "request_id": request_id}


@bp.activity_trigger(input_name="input")
def process_rejected_request(input: Dict[str, Any]) -> Dict[str, Any]:
    """
    Activity: Handle a rejected request.
    """
    request_id = input.get("request_id")
    reason = input.get("rejection_reason")
    
    logging.info(f"Handling rejected request: {request_id}, reason: {reason}")
    
    # Your rejection handling logic here
    
    return {"handled": True, "request_id": request_id}


# =============================================================================
# HTTP CLIENT/STARTER FUNCTIONS
# =============================================================================
# These are the HTTP triggers that start orchestrations


@bp.route(route="orchestration/sync", methods=["POST"])
@bp.durable_client_input(client_name="client")
async def start_sync_orchestration(req: func.HttpRequest, client: df.DurableOrchestrationClient) -> func.HttpResponse:
    """
    HTTP Trigger: Starts the Dynamics to NetSuite sync workflow.
    
    POST /orchestration/sync
    Body: {"entity_name": "accounts", "batch_size": 100}
    
    Returns immediately with a status URL that can be polled for completion.
    """
    try:
        req_body = req.get_json()
    except:
        req_body = {}
    
    # Start the orchestration (returns immediately!)
    instance_id = await client.start_new(
        orchestration_function_name="sync_dynamics_to_netsuite_orchestrator",
        client_input=req_body
    )
    
    logging.info(f"Started orchestration: {instance_id}")
    
    # Return management URLs for checking status, terminating, etc.
    return client.create_check_status_response(req, instance_id)


@bp.route(route="orchestration/parallel-sync", methods=["POST"])
@bp.durable_client_input(client_name="client")
async def start_parallel_sync(req: func.HttpRequest, client: df.DurableOrchestrationClient) -> func.HttpResponse:
    """
    HTTP Trigger: Starts parallel batch processing.
    
    POST /orchestration/parallel-sync
    Body: {"records": [{"id": "1", ...}, {"id": "2", ...}]}
    """
    try:
        req_body = req.get_json()
    except:
        req_body = {"records": []}
    
    instance_id = await client.start_new(
        orchestration_function_name="parallel_batch_sync_orchestrator",
        client_input=req_body
    )
    
    return client.create_check_status_response(req, instance_id)


@bp.route(route="orchestration/approval", methods=["POST"])
@bp.durable_client_input(client_name="client")
async def start_approval_workflow(req: func.HttpRequest, client: df.DurableOrchestrationClient) -> func.HttpResponse:
    """
    HTTP Trigger: Starts an approval workflow.
    
    POST /orchestration/approval
    Body: {"request_id": "REQ-001", "approver_email": "approver@example.com", "timeout_hours": 72}
    """
    try:
        req_body = req.get_json()
    except:
        return func.HttpResponse(
            json.dumps({"error": "Request body must contain valid JSON"}),
            status_code=400,
            mimetype="application/json"
        )
    
    instance_id = await client.start_new(
        orchestration_function_name="approval_workflow_orchestrator",
        client_input=req_body
    )
    
    return client.create_check_status_response(req, instance_id)


@bp.route(route="orchestration/{instance_id}/approve", methods=["POST"])
@bp.durable_client_input(client_name="client")
async def submit_approval(req: func.HttpRequest, client: df.DurableOrchestrationClient) -> func.HttpResponse:
    """
    HTTP Trigger: Submit approval decision for a waiting orchestration.
    
    POST /orchestration/{instance_id}/approve
    Body: {"approved": true} or {"approved": false, "reason": "Not authorized"}
    
    This raises the "ApprovalReceived" event that the orchestrator is waiting for.
    """
    instance_id = req.route_params.get("instance_id")
    
    try:
        req_body = req.get_json()
    except:
        return func.HttpResponse(
            json.dumps({"error": "Request body must contain valid JSON"}),
            status_code=400,
            mimetype="application/json"
        )
    
    # Raise the event that the orchestrator is waiting for
    await client.raise_event(
        instance_id=instance_id,
        event_name="ApprovalReceived",
        event_data=req_body
    )
    
    return func.HttpResponse(
        json.dumps({
            "message": f"Approval event sent to orchestration {instance_id}",
            "approval_data": req_body
        }),
        status_code=200,
        mimetype="application/json"
    )


@bp.route(route="orchestration/{instance_id}/status", methods=["GET"])
@bp.durable_client_input(client_name="client")
async def get_orchestration_status(req: func.HttpRequest, client: df.DurableOrchestrationClient) -> func.HttpResponse:
    """
    HTTP Trigger: Get the status of an orchestration.
    
    GET /orchestration/{instance_id}/status
    """
    instance_id = req.route_params.get("instance_id")
    
    status = await client.get_status(instance_id)
    
    if status is None:
        return func.HttpResponse(
            json.dumps({"error": f"Orchestration {instance_id} not found"}),
            status_code=404,
            mimetype="application/json"
        )
    
    return func.HttpResponse(
        json.dumps({
            "instance_id": status.instance_id,
            "runtime_status": status.runtime_status.name if status.runtime_status else None,
            "created_time": str(status.created_time) if status.created_time else None,
            "last_updated_time": str(status.last_updated_time) if status.last_updated_time else None,
            "output": status.output,
            "custom_status": status.custom_status
        }),
        status_code=200,
        mimetype="application/json"
    )


@bp.route(route="orchestration/{instance_id}/terminate", methods=["POST"])
@bp.durable_client_input(client_name="client")
async def terminate_orchestration(req: func.HttpRequest, client: df.DurableOrchestrationClient) -> func.HttpResponse:
    """
    HTTP Trigger: Terminate a running orchestration.
    
    POST /orchestration/{instance_id}/terminate
    Body: {"reason": "Cancelled by user"}
    """
    instance_id = req.route_params.get("instance_id")
    
    try:
        req_body = req.get_json()
        reason = req_body.get("reason", "Terminated via API")
    except:
        reason = "Terminated via API"
    
    await client.terminate(instance_id, reason)
    
    return func.HttpResponse(
        json.dumps({
            "message": f"Orchestration {instance_id} terminated",
            "reason": reason
        }),
        status_code=200,
        mimetype="application/json"
    )
