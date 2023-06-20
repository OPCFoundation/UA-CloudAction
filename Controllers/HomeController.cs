
namespace UACloudAction
{
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.AspNetCore.Mvc;

    [Authorize]
    public class HomeController : Controller
    {
        private readonly ActionProcessor _processor;

        public HomeController(ActionProcessor processor)
        {
            _processor = processor;
        }

        public IActionResult Index()
        {
            StatusModel model = new StatusModel
            {
                Status = _processor.Running? "Running" : "Not Running",
                ConnectionToADX = _processor.ConnectionToADX,
                ConnectionToBroker = _processor.ConnectionToBroker,
                ConnectionToUACloudCommander = _processor.ConnectionToUACloudCommander
            };

            return View(model);
        }

        public IActionResult Privacy()
        {
            return View();
        }
    }
}
