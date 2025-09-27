using Demo.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Reflection;
using System.Security.Claims;
namespace Demo.Controllers;

[Authorize(Roles ="JobSeeker")]
public class JobApplyController : BaseController
{
    private readonly DB db;
    private readonly IWebHostEnvironment en;
    private readonly Helper hp;


    public JobApplyController(DB db, IWebHostEnvironment en, Helper hp)
    {
        this.db = db;
        this.en = en;
        this.hp = hp;
    }

    private void DebugModelStateErrors()
    {
        foreach (var entry in ModelState)
        {
            foreach (var error in entry.Value.Errors)
            {
                Debug.WriteLine($"Error in {entry.Key}: {error.ErrorMessage}");
            }
        }
    }

    // 假设用户已经登录
    private User GetCurrentUser()
    {
        if (!User.Identity.IsAuthenticated)
            return null;

        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
            return null;

        var user = db.Users.FirstOrDefault(u => u.Id == userId);
        return user;
    }

    private void LoadDropdowns(ApplyPageVM vm)
    {
        // Source
        vm.Application.Sources = new List<SelectListItem>
{
    new SelectListItem { Value = "", Text = "--- Please select a source ---" } // 默认项
}
        .Concat(
            Enum.GetValues(typeof(ApplicationSource))
                .Cast<ApplicationSource>()
                .Select(e => new SelectListItem
                {
                    Value = e.ToString(),
                    Text = e.ToString()
                })
        ).ToList();

        // NoticeTime
        vm.Application.NoticeTimes = new List<SelectListItem>
{
    new SelectListItem { Value = "", Text = "--- Please select your expected notice period ---" }
}
        .Concat(
            Enum.GetValues(typeof(NoticeTime))
                .Cast<NoticeTime>()
                .Select(e => new SelectListItem
                {
                    Value = e.ToString(),
                    Text = e.GetType()
                         .GetMember(e.ToString())
                         .First()
                         .GetCustomAttribute<DisplayAttribute>()?.Name ?? e.ToString()
                })
        ).ToList();

        // SalaryExpected
        vm.Application.SalaryExpecteds = new List<SelectListItem>
{
    new SelectListItem { Value = "", Text = "--- Please select your expected salary in month ---" }
}
        .Concat(
            Enum.GetValues(typeof(SalaryExpected))
                .Cast<SalaryExpected>()
                .Select(e => new SelectListItem
                {
                    Value = e.ToString(),
                    Text = e.GetType()
                         .GetMember(e.ToString())
                         .First()
                         .GetCustomAttribute<DisplayAttribute>()?.Name ?? e.ToString()
                })
        ).ToList();
    }


    public IActionResult Apply(string jobId)
    {
        var currentUser = GetCurrentUser(); // 模拟登录
        if (currentUser == null)
        {
            // 如果没有用户就重定向到登录页
            return RedirectToAction("Login", "Account");// 登录检查
        }

        var job = db.Jobs.Find(jobId);
        if (job == null)
            return NotFound();

        var profileResume = db.Resumes.FirstOrDefault(u => u.UserId == currentUser.Id);
        var vm = new ApplyPageVM
        {
            Application = new ApplicationVM
            {
                JobId = job.Id,
                UserId = currentUser.Id,
                Source = null,          // 或者 ""
                SalaryExpected = null,  // 或者 ""
                NoticeTime = null       // 或者 ""
            },
            HasResume = profileResume != null // 前端用这个判断是否已有 Resume
        };// create VM



        LoadDropdowns(vm); // load the list down menu 

        ViewBag.Job = job; // get the job data from job table 
        return View(vm);
    }


    [HttpPost]
    public IActionResult Apply(ApplyPageVM vm, string includeResume)
    {
        var currentUser = GetCurrentUser();
        if (currentUser == null)
            return RedirectToAction("Login", "Account");

        // 手动补充 UserId
        vm.Application.UserId = currentUser.Id;
        var job = db.Jobs.Find(vm.Application.JobId);
        if (job == null)
            return NotFound();


        // 表单验证
        if (!ModelState.IsValid)
        {
            DebugModelStateErrors();
            LoadDropdowns(vm);
            ViewBag.Job = job;
            return View(vm);
        }

        // 检查 Profile 和 Resume
        var education = db.Educations.FirstOrDefault(u => u.UserId == currentUser.Id);



        if (education == null
            || string.IsNullOrEmpty(currentUser.FirstName)
            || string.IsNullOrEmpty(currentUser.LastName)
            || string.IsNullOrEmpty(currentUser.PhoneNumber)
            || string.IsNullOrEmpty(currentUser.Location))
        {
            SetFlashMessage(FlashMessageType.Warning, "Please complete your Information and Education before applying!");
            return RedirectToAction("Index", "Profile");
        }

        // 如果用户点击上传 Resume，但 Profile 还没有 Resume
        var profileResume = db.Resumes.FirstOrDefault(u => u.UserId == currentUser.Id);
        if (!string.IsNullOrEmpty(includeResume) && includeResume == "true" && profileResume == null)
        {
            // 用户希望上传简历，但 Profile 里没有简历
            if (profileResume == null)
            {

                SetFlashMessage(FlashMessageType.Warning, "Please upload your resume in your profile first.");
                return RedirectToAction("Resume", "Profile"); // 跳到 Profile 上传页面
            }
        }

        bool Exist_Apply = db.Applications.Any(j => j.UserId == currentUser.Id && j.JobId == job.Id);
        if (Exist_Apply) // verify whether the job is exist for the specific account 
        {
            ModelState.AddModelError("", "You have already applied for this job.");
            ViewBag.Job = job;
            LoadDropdowns(vm);
            return View(vm);
        }

        // 按数字部分排序生成ID
        var maxId = db.Applications
            .Where(j => j.Id.StartsWith("A")) // 只取以 "J" 开头的岗位记录
            .Select(j => j.Id.Substring(1)) // 取 Id 的数字部分（去掉开头的 "J"）
            .AsEnumerable()                 // 把数据拉到内存中，EF Core 可以在内存中执行 int.Parse
            .Select(s => int.Parse(s))// 把字符串数字转换成整数
            .DefaultIfEmpty(0) // 如果数据库中没有记录，默认最大值为 0
            .Max(); // 取出最大值，用于生成下一个 ID
        int nextNumber = maxId + 1;
        vm.Application.Id = "A" + nextNumber.ToString("D3"); // 生成唯一 ID

        // 保存 Application
        var application = new Application
        {
            Id = vm.Application.Id,
            JobId = job.Id,
            UserId = currentUser.Id,
            Resume = profileResume,
            Source = vm.Application.Source.Value,
            NoticeTime = vm.Application.NoticeTime.Value,
            SalaryExpected = vm.Application.SalaryExpected.Value,
            Status = ApplicationStatus.Pending,
            CreatedAt = DateTime.Now,
        };
        db.Applications.Add(application);
        db.SaveChanges();

        // audit log
        var log = new AuditLog
        {
            UserId = currentUser.Id,
            TableName = "Applications",
            ActionType = "Create",
            RecordId = application.Id,
            Changes = $"Applied for job: {application.JobId}"
        };
        db.AuditLogs.Add(log);
        db.SaveChanges();

        SetFlashMessage(FlashMessageType.Warning, "Application submitted successfully!");
        return RedirectToAction("ApplyList"); // 成功跳转页面
    }

    public IActionResult ApplyList()
    {
        var currentUser = GetCurrentUser();
        if (currentUser == null)
            return RedirectToAction("Login", "Account");

        var applications = db.Applications
                  .Include(a => a.Job)  // ✅ 必须 Include
                  .Where(a => a.UserId == currentUser.Id)
                  .OrderByDescending(a => a.CreatedAt)
                  .Select(a => new ApplicationListVM
                  {
                      ApplicationId = a.Id,
                      CreatedAt = a.CreatedAt,
                      Status = a.Status,
                      HiredDate = a.HiredDate,
                      JobTitle = a.Job.Title,
                      CompanyName = a.Job.User.CompanyName
                  })
                  .ToList();

        return View(applications);
    }


    public IActionResult Detail(string applicationId)
    {
        var currentUser = GetCurrentUser();
        if (currentUser == null)
            return RedirectToAction("Login", "Account");

        var app = db.Applications
                    .Include(a => a.Job)  // ✅ 确保能拿到 Job 信息
                    .FirstOrDefault(a => a.Id == applicationId && a.UserId == currentUser.Id);

        if (app == null)
            return NotFound();

        var vm = new ApplicationDetailVM
        {
            ApplicationId = app.Id,
            JobTitle = app.Job.Title,
            //CompanyName = app.Job.CompanyName,
            JobLogo = app.Job.LogoImageUrl,
            Source = app.Source.GetDisplayName(),    // using extension to display the msg           
            SalaryExpected = app.SalaryExpected.GetDisplayName(),
            NoticeTime = app.NoticeTime.GetDisplayName(),
            Status = app.Status,
            CreatedAt = app.CreatedAt,
            HiredDate = app.HiredDate
        };

        return View(vm);
    }

    [HttpPost]
    public IActionResult DeleteApplication(string? id)
    {
        var app = db.Applications.Find(id);
        if (app != null)
        {
            db.Applications.Remove(app);
            db.SaveChanges();

            // audit log
            var log = new AuditLog
            {
                UserId = app.UserId,
                TableName = "Applications",
                ActionType = "Delete",
                RecordId = app.Id,
                Changes = $"Withdrawn application for job: {app.JobId}"
            };
            db.AuditLogs.Add(log);
            db.SaveChanges();

            SetFlashMessage(FlashMessageType.Warning, "Application canceled successfully.");
        }

        return RedirectToAction("ApplyList", "JobApply");  // 删除后回到之前页面
    }

}