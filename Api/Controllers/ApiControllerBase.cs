using AlfaHackaton.Controllers.Controllers;
using AlfaHackaton.Result;
using InsightService.Models;
using Microsoft.AspNetCore.Mvc;
namespace InsightService.Controllers;

public abstract class ApiControllerBase : BaseController
{
    protected new IActionResult OkOrProblem<T>(Result<T> result)
    {
        if (result.Error is ApiError error)
        {
            return StatusCode(error.StatusCode, new
            {
                code = error.Code,
                message = error.Title
            });
        }
        return base.OkOrProblem(result);
    }
}
