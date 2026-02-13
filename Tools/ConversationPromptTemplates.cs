namespace fluid_durable_agent.Tools;

public static class ConversationPromptTemplates
{
   /// <summary>
   /// Gets the current date context string with today's date
   /// </summary>
   public static string TodayDateContext => $"Today's date is {DateTime.Now:MM/dd/yyyy} (MM/DD/YYYY). Use this information to evaluate any date-related fields or questions.";

  
}
