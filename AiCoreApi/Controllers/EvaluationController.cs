using AiCoreApi.Authorization;
using AiCoreApi.Authorization.Attributes;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Models.ViewModels;
using AiCoreApi.Services.ControllersServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiCoreApi.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/v1/evaluation")]
    public class EvaluationController : ControllerBase
    {
        private readonly IEvaluationService _evaluationService;
        public EvaluationController(IEvaluationService evaluationService)           
        {
            _evaluationService = evaluationService;
        }

        [CombinedAuthorize]
        [RoleAuthorize(Role.Admin, Role.Developer)]
        [HttpGet("{evaluationId}")]
        public async Task<IActionResult> Get(int evaluationId)
        {
            var currentUser = this.GetLogin();
            if (currentUser == null) return Unauthorized();

            var result = await _evaluationService.GetById(evaluationId);
            return Ok(result);
        }

        [CombinedAuthorize]
        [RoleAuthorize(Role.Admin, Role.Developer)]
        [HttpGet("history/{evaluationId}")]
        public async Task<IActionResult> ListHistory(int evaluationId)
        {
            var currentUser = this.GetLogin();
            if (currentUser == null) return Unauthorized();

            var result = await _evaluationService.ListHistory(evaluationId);
            return Ok(result);
        }

        [CombinedAuthorize]
        [RoleAuthorize(Role.Admin, Role.Developer)]
        [HttpGet("history/details/{evaluationHistoryId}")]
        public async Task<IActionResult> GetHistoryItem(int evaluationHistoryId)
        {
            var currentUser = this.GetLogin();
            if (currentUser == null) return Unauthorized();

            var result = await _evaluationService.GetHistoryItem(evaluationHistoryId);
            return Ok(result);
        }

        [CombinedAuthorize]
        [RoleAuthorize(Role.Admin, Role.Developer)]
        [HttpGet]
        public async Task<IActionResult> List()
        {
            var currentUser = this.GetLogin();
            if (currentUser == null) return Unauthorized();

            var result = await _evaluationService.List();
            return Ok(result);
        }

        [CombinedAuthorize]
        [RoleAuthorize(Role.Admin, Role.Developer)]
        [HttpDelete("{evaluationId}")]
        public async Task<IActionResult> Delete(int evaluationId)
        {
            var currentUser = this.GetLogin();
            if (currentUser == null) return Unauthorized();

            await _evaluationService.Delete(evaluationId);
            return Ok();
        }

        [CombinedAuthorize]
        [RoleAuthorize(Role.Admin, Role.Developer)]
        [HttpPost("run/{evaluationId}")]
        public async Task<IActionResult> Run(int evaluationId)
        {
            var currentUser = this.GetLogin();
            if (currentUser == null) return Unauthorized();

            await _evaluationService.Run(evaluationId);
            return Ok();
        }

        [CombinedAuthorize]
        [RoleAuthorize(Role.Admin, Role.Developer)]
        [HttpPost]
        public async Task<IActionResult> Add([FromBody] EvaluationViewModel evaluationViewModel)
        {
            var currentUser = this.GetLogin();
            if (currentUser == null) return Unauthorized();

            evaluationViewModel.CreatedBy = currentUser;
            evaluationViewModel.Created = DateTime.UtcNow;
            var model = await _evaluationService.Add(evaluationViewModel);

            return Ok(model != null);
        }

        [CombinedAuthorize]
        [RoleAuthorize(Role.Admin, Role.Developer)]
        [HttpPut("{evaluationId}")]
        public async Task<IActionResult> Update([FromBody] EvaluationViewModel evaluationViewModel)
        {
            var currentUser = this.GetLogin();
            if (currentUser == null) return Unauthorized();

            var model = await _evaluationService.Update(evaluationViewModel);

            return Ok(model != null);
        }
    }
}
