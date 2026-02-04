using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using fluid_durable_agent.Models;
using fluid_durable_agent.Services;

namespace fluid_durable_agent.Agents;

public class Agent_CommodityCodeLookup
{
    private readonly IChatClient _chatClient;
    private readonly BlobStorageService _blobStorageService;
    private readonly ILogger<Agent_CommodityCodeLookup> _logger;
    private const string CommodityCodesContainer = "commodity-codes";
    
    // Category definitions - these map text descriptions to category file numbers
    private static readonly Dictionary<string, string> CategoryMappings = new()
    {
        { "32", "Electronic Components and Supplies" },
        { "41", "Laboratory and Measuring and Observing and Testing Equipment" },
        { "42", "Medical Equipment and Accessories and Supplies" },
        { "43", "Information Technology Broadcasting and Telecommunications" },
        { "44", "Office Equipment and Accessories and Supplies" },
        { "46", "Defense and Law Enforcement and Security and Safety Equipment and Supplies" },
        { "55", "Published Products" },
        { "60", "Musical Instruments and Games and Toys and Arts and Crafts and Educational Eq" },
        { "64", "Financial Instruments, Products, Contracts and Agreements" },
        { "70", "Farming and Fishing and Forestry and Wildlife Contracting Services" },
        { "72", "Building and Facility Construction and Maintenance Services" },
        { "80", "Management and Business Professionals and Administrative Services" },
        { "81", "Engineering and Research and Technology Based Services" },
        { "82", "Editorial and Design and Graphic and Fine Art Services" },
        { "84", "Financial and Insurance Services" },
        { "85", "Healthcare Services" },
        { "86", "Education and Training Services" },
        { "91", "Personal and Domestic Services" },
        { "92", "National Defense and Public Order and Security and Safety Services" },
        { "8510", "Comprehensive health services" },
        { "8511", "Disease prevention and control" },
        { "8512", "Medical practice" },
        { "8513", "Medical science research and experimentation" },
        { "8514", "Alternative and holistic medicine" },
        { "8515", "Food and nutrition services" },
        { "8516", "Medical Surgical Equipment Maintenance Refurbishment a" },
        { "8517", "Death and dying support services" },
        { "8521", "Diagnoses of infectious and parasitic diseases-part a" },
        { "8522", "Diagnoses of infectious and parasitic diseases-part b" },
        { "8525", "Diagnoses of neoplasms" },
        { "8526", "Diagnoses of endocrine, nutritional and metabolic diseases" },
        { "8527", "Diagnoses of mental and behavioral disorders" },
        { "8528", "Diagnoses of diseases of the nervous system" },
        { "8529", "Diagnoses of diseases of the eye and adnexa" },
        { "8530", "Diagnoses of diseases of the ear and mastoid process" },
        { "8531", "Diagnoses of diseases of the circulatory system" },
        { "8532", "Diagnoses of diseases of the blood and blood-forming orga" },
        { "8533", "Diagnoses of diseases of the digestive system" },
        { "8534", "Diagnoses of diseases of the respiratory system" },
        { "8535", "Diagnoses of diseases of the skin and subcutaneous tissue" },
        { "8536", "Diagnoses of diseases of the genitourinary system" },
        { "8537", "Diagnoses of diseases of the musculoskeletal system and c" },
        { "8538", "Diagnoses of certain conditions originating in the perinatal" },
        { "8539", "Diagnoses of pregnancy, childbirth conditions and the puer" },
        { "8540", "Diagnoses of congenital malformations, deformations, and" },
        { "8541", "Diagnoses of symptoms, signs and abnormal clinical and la" },
        { "8542", "Diagnoses of injury, poisoning and certain other consequen" },
        { "8543", "Diagnoses of injury, poisoning and certain other consequen" },
        { "8544", "Diagnoses of external causes of morbidity and mortality" },
        { "8545", "The diagnosis of factors influencing health status and conta" },
        { "8547", "Surgical interventions or procedures of central nervous sys" },
        { "8548", "Surgical interventions or procedures of peripheral nervous" },
        { "8549", "Surgical interventions or procedures of heart and great ves" },
        { "8550", "Surgical interventions or procedures of upper arteries" },
        { "8551", "Surgical interventions or procedures of lower arteries" },
        { "8552", "Surgical interventions or procedures of upper veins" },
        { "8553", "Surgical interventions or procedures of lower veins" },
        { "8554", "Surgical interventions or procedures of lymphatic and hem" },
        { "8555", "Surgical interventions or procedures of sensory functional" },
        { "8556", "Surgical interventions or procedures of sensory functional" },
        { "8557", "Surgical interventions or procedures of the respiratory syst" },
        { "8558", "Surgical interventions or procedures of the mouth and thro" },
        { "8559", "Surgical interventions or procedures of the gastrointestinal" },
        { "8560", "Surgical interventions or procedures of the gastrointestinal" },
        { "8561", "Surgical interventions or procedures of the hepatobiliary sy" },
        { "8562", "Surgical interventions or procedures of the endocrine syste" },
        { "8563", "Surgical interventions or procedures of the skin and breast" },
        { "8564", "Surgical interventions or procedures of the subcutaneous t" },
        { "8565", "Surgical interventions or procedures of muscles" },
        { "8566", "Surgical interventions or procedures of tendons" },
        { "8567", "Surgical interventions or procedures of bursae and ligamen" },
        { "8568", "Surgical interventions or procedures of head and facial bon" },
        { "8569", "Surgical interventions or procedures of upper bones" },
        { "8570", "Surgical interventions or procedures of lower bones" },
        { "8571", "Surgical interventions or procedures of upper joints" },
        { "8572", "Surgical interventions or procedures of lower joints" },
        { "8573", "Surgical interventions or procedures of urinary system" },
        { "8574", "Surgical interventions or procedures of female reproductiv" },
        { "8575", "Surgical interventions or procedures of male reproductive" },
        { "8576", "Surgical interventions or procedures of anatomic regions-g" },
        { "8577", "Surgical interventions or procedures of anatomic regions-u" },
        { "8578", "Surgical interventions or procedures of anatomic regions-lo" },
        { "8579", "Obstetric interventions or procedures, pregnancy" },
        { "8580", "Placement interventions or procedures, anatomic regions a" },
        { "8581", "Administrative interventions or procedures, physiological s" },
        { "8582", "Measurement and monitoring interventions or procedures" },
        { "8583", "Extracorporeal or systemic assistance, performance and th" },
        { "8584", "Osteopathic interventions or procedures by anatomical reg" },
        { "8586", "Chiropractic interventions or procedures by anatomical reg" },
        { "8587", "Imaging interventions or procedures by anatomical region" },
        { "8588", "Nuclear medicine interventions or procedures" },
        { "8589", "Radiation therapy interventions or procedures" },
        { "8590", "Physical rehabilitation interventions or procedures" },
        { "8591", "Mental health interventions or procedures" },
        { "8592", "Substance abuse interventions or procedures" }
    };

    public Agent_CommodityCodeLookup(IChatClient chatClient, BlobStorageService blobStorageService, ILogger<Agent_CommodityCodeLookup> logger)
    {
        _chatClient = chatClient;
        _blobStorageService = blobStorageService;
        _logger = logger;
    }

    public async Task<CommodityCodeResult> LookupCommodityCodeAsync(string inputText)
    {
        try
        {
            // Step 1: Determine which category best matches the input text
            var categoryNumber = await DetermineCategoryAsync(inputText);
            
            if (string.IsNullOrEmpty(categoryNumber))
            {
                _logger.LogWarning("Failed to determine category for input text");
                return new CommodityCodeResult 
                { 
                    Code = "ERROR", 
                    Description = "Unable to determine commodity category" 
                };
            }

            _logger.LogInformation("Determined category {Category} for commodity lookup", categoryNumber);

            // Step 2: Load the category file from blob storage
            var categoryFileName = $"{categoryNumber}.txt";
            var categoryContent = await _blobStorageService.ReadFileAsync("", categoryFileName, CommodityCodesContainer);
            
            if (string.IsNullOrEmpty(categoryContent))
            {
                _logger.LogWarning("Category file {FileName} not found in blob storage", categoryFileName);
                return new CommodityCodeResult 
                { 
                    Code = "ERROR", 
                    Description = $"Category file {categoryFileName} not found" 
                };
            }

            _logger.LogInformation("Loaded category file {FileName} with {Length} characters", categoryFileName, categoryContent.Length);

            // Step 3: Find the specific commodity code within the category
            var commodityCode = await FindCommodityCodeAsync(inputText, categoryContent);
            
            return commodityCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during commodity code lookup");
            return new CommodityCodeResult 
            { 
                Code = "ERROR", 
                Description = $"Lookup failed: {ex.Message}" 
            };
        }
    }

    private async Task<string> DetermineCategoryAsync(string inputText)
    {
        var categoriesList = string.Join("\n", CategoryMappings.Select(c => $"{c.Key}. {c.Value}"));
        
        var prompt = $@"You are a commodity classification expert. Based on the following description, determine which category number best matches.

CATEGORIES:
{categoriesList}

INPUT TEXT:
{inputText}

Return ONLY the category number that best matches the input text. Return just the number, nothing else.";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a precise commodity classification assistant. Return only the category number."),
            new(ChatRole.User, prompt)
        };

        var response = await _chatClient.GetResponseAsync(messages);
        var categoryNumber = response.Text?.Trim();
        
        // Validate the response is a valid category number
        if (!string.IsNullOrEmpty(categoryNumber) && CategoryMappings.ContainsKey(categoryNumber))
        {
            return categoryNumber;
        }

        _logger.LogWarning("Invalid category number returned: {CategoryNumber}", categoryNumber);
        return string.Empty;
    }

    private async Task<CommodityCodeResult> FindCommodityCodeAsync(string inputText, string categoryContent)
    {
        var prompt = $@"You are a commodity code matching expert. You will be given a list of commodity codes with descriptions, and you need to find the best matching code for the given text.

COMMODITY CODES AND DESCRIPTIONS:
{categoryContent}

INPUT TEXT TO MATCH:
{inputText}

Carefully analyze the input text and find the commodity code that most closely matches it. Return your response as a JSON object with this exact structure:
{{
  ""code"": ""<the commodity code>"",
  ""description"": ""<the code's description>""
}}

Return ONLY the JSON object, nothing else.";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a precise commodity code matching assistant. Return only valid JSON."),
            new(ChatRole.User, prompt)
        };

        var response = await _chatClient.GetResponseAsync(messages);
        var jsonResponse = response.Text?.Trim();

        if (string.IsNullOrEmpty(jsonResponse))
        {
            return new CommodityCodeResult 
            { 
                Code = "ERROR", 
                Description = "No response from AI model" 
            };
        }

        // Try to parse the JSON response
        try
        {
            var result = JsonSerializer.Deserialize<CommodityCodeResult>(jsonResponse, 
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            
            if (result != null)
            {
                return result;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse commodity code JSON response: {Response}", jsonResponse);
        }

        return new CommodityCodeResult 
        { 
            Code = "ERROR", 
            Description = "Failed to parse AI response" 
        };
    }
}
