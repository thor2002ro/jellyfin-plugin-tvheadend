namespace TVHeadEnd.Configuration
{
    public static class StreamingMethods
    {
        public const string HttpTicket = "HttpTicket";
        public const string HttpBasic = "HttpBasic";
        public const string Htsp = "Htsp";

        public static string GetEffective(string streamingMethod)
        {
            return streamingMethod == HttpBasic || streamingMethod == HttpTicket || streamingMethod == Htsp
                ? streamingMethod
                : Htsp;
        }
    }
}
