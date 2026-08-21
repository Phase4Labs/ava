using System.Text.Json;

namespace get_assessment_no_graph;

/// <summary>
/// JSON Schemas used by the OpenAI Responses API Structured Outputs feature.
/// Keeping the schemas in code prevents prompt/schema drift and makes malformed
/// model responses much less likely.
/// </summary>
public static class OpenAiJsonSchemas
{
    public static readonly JsonElement ExecutionCardV1 = Parse("""
    {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "schema_version": { "type": "integer", "enum": [1] },
        "verdict": { "type": "string", "enum": ["TRADE", "NO_TRADE"] },
        "scenarios": {
          "type": "array",
          "maxItems": 3,
          "items": {
            "type": "object",
            "additionalProperties": false,
            "properties": {
              "scenario_rank": { "type": "integer", "minimum": 1, "maximum": 3 },
              "direction": { "type": "string", "enum": ["long", "short"] },
              "entry_type": {
                "type": "string",
                "enum": ["reclaim_hold", "break_hold", "fade_pop", "vwap_reclaim", "overextension_fade"]
              },
              "scenario_prob": { "type": "number", "minimum": 0, "maximum": 1 },
              "success_prob": { "type": "number", "minimum": 0, "maximum": 1 },
              "entry_low": { "type": ["number", "null"] },
              "entry_high": { "type": ["number", "null"] },
              "stop_price": { "type": ["number", "null"] },
              "t1": { "type": ["number", "null"] },
              "t2": { "type": ["number", "null"] },
              "runner": { "type": ["number", "null"] },
              "grade": { "type": "string", "enum": ["A", "B", "C", "D", "F"] },
              "grade_rationale": { "type": ["string", "null"] }
            },
            "required": [
              "scenario_rank", "direction", "entry_type", "scenario_prob", "success_prob",
              "entry_low", "entry_high", "stop_price", "t1", "t2", "runner",
              "grade", "grade_rationale"
            ]
          }
        }
      },
      "required": ["schema_version", "verdict", "scenarios"]
    }
    """);

    /// <summary>
    /// Local/shadow execution-card schema. If a scenario exists, the geometry required
    /// by AVA's promoted structural gate must be present as numbers. Optional later
    /// targets remain nullable. This does not change the cloud GPT schema.
    /// </summary>
    public static readonly JsonElement LocalExecutableCardV1 = Parse("""
    {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "schema_version": { "type": "integer", "enum": [1] },
        "verdict": { "type": "string", "enum": ["TRADE", "NO_TRADE"] },
        "scenarios": {
          "type": "array",
          "maxItems": 3,
          "items": {
            "type": "object",
            "additionalProperties": false,
            "properties": {
              "scenario_rank": { "type": "integer", "minimum": 1, "maximum": 3 },
              "direction": { "type": "string", "enum": ["long", "short"] },
              "entry_type": {
                "type": "string",
                "enum": ["reclaim_hold", "break_hold", "fade_pop", "vwap_reclaim", "overextension_fade"]
              },
              "scenario_prob": { "type": "number", "minimum": 0, "maximum": 1 },
              "success_prob": { "type": "number", "minimum": 0, "maximum": 1 },
              "entry_low": { "type": "number" },
              "entry_high": { "type": "number" },
              "stop_price": { "type": "number" },
              "t1": { "type": "number" },
              "t2": { "type": ["number", "null"] },
              "runner": { "type": ["number", "null"] },
              "grade": { "type": "string", "enum": ["A", "B", "C", "D", "F"] },
              "grade_rationale": { "type": ["string", "null"] }
            },
            "required": [
              "scenario_rank", "direction", "entry_type", "scenario_prob", "success_prob",
              "entry_low", "entry_high", "stop_price", "t1", "t2", "runner",
              "grade", "grade_rationale"
            ]
          }
        }
      },
      "required": ["schema_version", "verdict", "scenarios"]
    }
    """);

    public static readonly JsonElement ReEvalV1 = Parse("""
    {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "stop_price": { "type": "number" },
        "stop_type": {
          "type": "string",
          "enum": ["hard", "profit_protection", "soft_warning"]
        },
        "t1": { "type": "number" },
        "t2": { "type": ["number", "null"] },
        "runner": { "type": ["number", "null"] },
        "runner_justified": { "type": "boolean" },
        "confidence": { "type": "number", "minimum": 0, "maximum": 1 },
        "rationale": { "type": "string" }
      },
      "required": [
        "stop_price", "stop_type", "t1", "t2", "runner",
        "runner_justified", "confidence", "rationale"
      ]
    }
    """);

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
