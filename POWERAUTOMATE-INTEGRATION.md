# PowerAutomate Integration Guide

## Message Endpoint Response Schema

This document describes how to call the message endpoint from PowerAutomate and parse the response.

### Endpoint Details

**URL:** `POST /api/session/{instanceId}/message`

**Request Body:** Plain text message from the user

**Response:** JSON object with optional fields (not all fields are present in every response)

---

## PowerAutomate Setup

### Step 1: HTTP Request Action

Add an **HTTP** action to your PowerAutomate flow with the following settings:

- **Method:** POST
- **URI:** `https://your-function-app.azurewebsites.net/api/session/{instanceId}/message`
- **Headers:**
  - `Content-Type`: `text/plain`
- **Body:** Your message text (e.g., dynamic content from user input)

### Step 2: Parse JSON Action

Add a **Parse JSON** action immediately after the HTTP action:

- **Content:** Select `Body` from the HTTP action output
- **Schema:** Copy and paste the schema from `powerautomate-parse-json-schema.json`

---

## Response Fields Reference

### Always Present
- **status** (string): Either `"fields_updated"` or `"ok"`

### Conditionally Present

#### Field Updates
- **newFieldValues** (array): Present when fields were extracted/updated
  - Each item contains:
    - `fieldName` (string): Field identifier
    - `value` (any type): The field value (can be string, number, boolean, array, or object)
    - `note` (string, optional): Notes about the value
    - `inferred` (boolean, optional): True if AI inferred this value
    - `drafted` (boolean, optional): True if AI drafted this content

#### Validation Results
- **errors** (array): Present when validation errors occurred
  - Each item contains:
    - `fieldId` (string): Field with the error
    - `concern` (string): Error description
    
- **warnings** (array): Present when validation warnings occurred
  - Each item contains:
    - `fieldId` (string): Field with the warning
    - `concern` (string): Warning description

#### Conversation Response Components
- **questionResponse** (string): AI's response to user's question
- **acknowledgeInputs** (string): Acknowledgment of user inputs
- **validationConcerns** (string): Concerns about validation
- **finalThoughts** (string): AI's final response/thoughts
- **fieldFocus** (string): ID of the next field to focus on

---

## PowerAutomate Conditions

Since not all fields are present in every response, use PowerAutomate conditions to check if a field exists before using it:

### Example: Check if errors exist

```
Condition: @not(equals(body('Parse_JSON')?['errors'], null))
```

### Example: Check if newFieldValues exists and has items

```
Condition: @and(not(equals(body('Parse_JSON')?['newFieldValues'], null)), greater(length(body('Parse_JSON')?['newFieldValues']), 0))
```

### Example: Check if finalThoughts exists

```
Condition: @not(empty(body('Parse_JSON')?['finalThoughts']))
```

---

## Sample Responses

### Response 1: Field Updated Successfully
```json
{
  "status": "fields_updated",
  "newFieldValues": [
    {
      "fieldName": "email",
      "value": "user@example.com",
      "note": "Extracted from message"
    }
  ],
  "acknowledgeInputs": "I've captured your email address.",
  "finalThoughts": "Thank you for providing your email. What's your phone number?",
  "fieldFocus": "phone"
}
```

### Response 2: Validation Error
```json
{
  "status": "fields_updated",
  "newFieldValues": [
    {
      "fieldName": "age",
      "value": "150"
    }
  ],
  "errors": [
    {
      "fieldId": "age",
      "concern": "Age value seems unrealistic (150 years old)"
    }
  ],
  "validationConcerns": "The age you provided seems unusually high.",
  "finalThoughts": "Could you please confirm your age?"
}
```

### Response 3: Simple Question Response
```json
{
  "status": "ok",
  "questionResponse": "This form collects your contact information for registration purposes.",
  "finalThoughts": "This form collects your contact information for registration purposes."
}
```

### Response 4: AI-Drafted Content
```json
{
  "status": "fields_updated",
  "newFieldValues": [
    {
      "fieldName": "description",
      "value": "I am writing to request information about...",
      "drafted": true,
      "note": "AI-drafted content"
    }
  ],
  "finalThoughts": "I've drafted a description for you. Please review and let me know if you'd like any changes."
}
```

---

## Best Practices

1. **Always check for field existence** before accessing optional fields
2. **Handle arrays properly** - check length before iterating
3. **Test with multiple scenarios** since responses vary based on user input
4. **Use the status field** to determine if fields were updated
5. **Display finalThoughts** to the user as the AI's primary response
6. **Check errors and warnings** arrays to provide validation feedback
7. **Use fieldFocus** to guide the UI to the next field

---

## Troubleshooting

### Issue: Parse JSON fails
**Solution:** Ensure you're using the schema from `powerautomate-parse-json-schema.json` exactly as provided. The schema handles optional fields properly.

### Issue: Can't access a field
**Solution:** That field may not be present in this particular response. Add a condition to check if the field exists before accessing it.

### Issue: newFieldValues is empty
**Solution:** This is normal when no fields were extracted from the message. Check the `status` field - it will be `"ok"` instead of `"fields_updated"`.

---

## Files Included

- **powerautomate-parse-json-schema.json**: Simplified schema for PowerAutomate's "Parse JSON" action
- **powerautomate-message-response-schema.json**: Full JSON Schema (JSON Schema Draft 07 format) for documentation and validation

Use the `powerautomate-parse-json-schema.json` file directly in PowerAutomate's Parse JSON action.
