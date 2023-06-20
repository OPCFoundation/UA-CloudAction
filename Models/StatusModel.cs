
namespace UACloudAction
{
    public class StatusModel
    {
        public string Status { get; set; } = string.Empty;

        public bool ConnectionToADX { get; set; } = false;

        public bool ConnectionToBroker { get; set; } = false;

        public bool ConnectionToUACloudCommander { get; set; } = false;
    }
}
