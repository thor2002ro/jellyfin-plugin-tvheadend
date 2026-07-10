namespace TVHeadEnd.Configuration
{
    public static class StreamingMethods
    {
        public const string HttpTicket = "HttpTicket";
        public const string HttpBasic = "HttpBasic";
        public const string Htsp = "Htsp";

        public static string GetEffective(string streamingMethod, bool enableSubsMaudios)
        {
            if (streamingMethod == HttpBasic || streamingMethod == Htsp)
            {
                return streamingMethod;
            }

            if (streamingMethod == HttpTicket)
            {
                return streamingMethod;
            }

            return enableSubsMaudios ? HttpBasic : Htsp;
        }
    }
}
