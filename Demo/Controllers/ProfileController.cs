using Demo.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Security.Claims;

namespace Demo.Controllers;

[Authorize(AuthenticationSchemes = "DefaultCookie", Roles = "JobSeeker")]
public class ProfileController : Controller
{
    private readonly DB _context;
    private readonly IWebHostEnvironment _env;

    public ProfileController(DB context, IWebHostEnvironment env)
    {
        _context = context;
        _env = env;
    }

    // 统一取当前用户
    private async Task<User?> GetCurrentUserAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return null;

        return await _context.Users
            .Include(u => u.Educations)
            .Include(u => u.JobExperiences)
            .Include(u => u.Resumes)
            .FirstOrDefaultAsync(u => u.Id == userId);
    }

    // ========== Index ==========
    public async Task<IActionResult> Index()
    {
        var user = await GetCurrentUserAsync();
        if (user == null) return NotFound();
        ViewData["Active"] = "Index";
        return View(user);
    }

    // ========== Resume 列表 ==========
    public async Task<IActionResult> Resume()
    {
        var user = await GetCurrentUserAsync();
        if (user == null) return NotFound();
        ViewData["Active"] = "Resume";
        return View(user.Resumes?.OrderByDescending(r => r.CreatedAt));
    }

    // 上传简历（支持多文件）
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadResume(IFormFile file)
    {
        var user = await GetCurrentUserAsync();
        if (user == null || file == null || file.Length == 0)
            return RedirectToAction(nameof(Resume));

        var ext = Path.GetExtension(file.FileName);
        var fileName = $"resume_{Guid.NewGuid()}{ext}";
        var savePath = Path.Combine(_env.WebRootPath, "uploads", "resumes");
        Directory.CreateDirectory(savePath);

        var fullPath = Path.Combine(savePath, fileName);
        using (var stream = new FileStream(fullPath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        var resume = new Resume
        {
            Id = await GenerateResumeId(),
            UserId = user.Id,
            ImageUrl = $"/uploads/resumes/{fileName}"
        };
        _context.Resumes.Add(resume);
        await _context.SaveChangesAsync();

        return RedirectToAction(nameof(Resume));
    }

    // 删除简历
    public async Task<IActionResult> DeleteResume(string id)
    {
        var resume = await _context.Resumes.FindAsync(id);
        if (resume != null)
        {
            _context.Resumes.Remove(resume);
            await _context.SaveChangesAsync();
            // 可选：删除物理文件
            var physicalPath = Path.Combine(_env.WebRootPath, resume.ImageUrl.TrimStart('/'));
            if (System.IO.File.Exists(physicalPath))
                System.IO.File.Delete(physicalPath);
        }
        return RedirectToAction(nameof(Resume));
    }

    // 生成ID: R001...
    private async Task<string> GenerateResumeId()
    {
        int counter = 1;
        string newId;
        do
        {
            newId = $"R{counter:000}";
            counter++;
        } while (await _context.Resumes.AnyAsync(r => r.Id == newId));
        return newId;
    }

    // ── Info ────────────────────────────────────────
    [HttpGet]
    public async Task<IActionResult> Info()
    {
        var u = await GetCurrentUserAsync();
        if (u == null) return NotFound();
        return View(new InfoViewModel
        {
            Email = u.Email,
            PhoneNumber = u.PhoneNumber,
            Location = u.Location,
            FirstName = u.FirstName,
            LastName = u.LastName
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Info(InfoViewModel vm)
    {
        if (!ModelState.IsValid) return View(vm);
        var u = await GetCurrentUserAsync();
        if (u == null) return NotFound();

        u.Email = vm.Email;
        u.PhoneNumber = vm.PhoneNumber;
        u.Location = vm.Location;
        u.FirstName = vm.FirstName;
        u.LastName = vm.LastName;
        u.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        // Audit log for profile info update
        var log = new AuditLog
        {
            UserId = u.Id,
            TableName = "Users",
            ActionType = "Update",
            RecordId = u.Id,
            Changes = $"Profile updated: {u.Email}, {u.PhoneNumber}, {u.Location}, {u.FirstName}, {u.LastName}"
        };
        _context.AuditLogs.Add(log);
        await _context.SaveChangesAsync();

        TempData["Success"] = "Information has been updated";
        return RedirectToAction(nameof(Index));
    }

    // ── Summary ─────────────────────────────────────
    [HttpGet]
    public async Task<IActionResult> Summary()
    {
        var u = await GetCurrentUserAsync();
        return View(new SummaryViewModel { Summary = u?.Summary });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Summary(SummaryViewModel vm)
    {
        if (!ModelState.IsValid) return View(vm);
        var u = await GetCurrentUserAsync();
        if (u == null) return NotFound();

        u.Summary = vm.Summary;
        u.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    // ── Career History ──────────────────────────────
    [HttpGet]
    public async Task<IActionResult> CareerHistory()
    {
        var u = await GetCurrentUserAsync();
        if (u == null) return NotFound();

        var vm = new CareerPageViewModel
        {
            ExistingJobExperiences = u.JobExperiences ?? new List<JobExperience>()
        };
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddJob(CareerPageViewModel vm)
    {
        if (!ModelState.IsValid)
        {
            // 重新带回已有列表，让页面验证失败时仍能显示数据
            var u = await GetCurrentUserAsync();
            vm.ExistingJobExperiences = u?.JobExperiences ?? new List<JobExperience>();
            return View("CareerHistory", vm);
        }

        if(vm.StartYear >= vm.EndYear && vm.StartMonth > vm.EndMonth)
        {
            var u = await GetCurrentUserAsync();
            vm.ExistingJobExperiences = u?.JobExperiences ?? new List<JobExperience>();
            ModelState.AddModelError("StartMonth", "Start year cannot more than End year!");
            return View("CareerHistory", vm);
        }

        var uCurrent = await GetCurrentUserAsync();
        if (uCurrent == null) return NotFound();

        var nextNum = (_context.JobExperiences.Count() + 1).ToString("D3");
        var je = new JobExperience
        {
            Id = $"JE{nextNum}",
            UserId = uCurrent.Id,
            JobTitle = vm.JobTitle,
            CompanyName = vm.CompanyName,
            StartMonth = vm.StartMonth,
            StartYear = vm.StartYear,
            EndMonth = vm.EndMonth,
            EndYear = vm.EndYear,
            StillInRole = vm.StillInRole
        };

        _context.JobExperiences.Add(je);
        await _context.SaveChangesAsync();

        return RedirectToAction(nameof(CareerHistory));
    }
    
    public async Task<IActionResult> DeleteJob(string id)
    {
        var career = await _context.JobExperiences.FindAsync(id);
        if (career != null)
        {
            _context.JobExperiences.Remove(career);
            await _context.SaveChangesAsync();

            var log = new AuditLog
            {
                UserId = career.UserId,
                TableName = "Career History",
                ActionType = "Delete",
                RecordId = career.Id,
                Changes = $"Deleted career history: {career.Id}"
            };
            _context.AuditLogs.Add(log);
            await _context.SaveChangesAsync();
        }
        return RedirectToAction(nameof(CareerHistory));
    }

    // ── Education ───────────────────────────────────
    [HttpGet]
    public async Task<IActionResult> Education()
    {
        var u = await GetCurrentUserAsync();
        if (u == null) return NotFound();

        var vm = new EducationPageViewModel
        {
            ExistingEducations = u.Educations ?? new List<Education>()
        };
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddEducation(EducationPageViewModel vm)
    {
        if (!ModelState.IsValid)
        {
            // 重新带回已有列表，让页面验证失败时仍能显示数据
            var u = await GetCurrentUserAsync();
            vm.ExistingEducations = u?.Educations ?? new List<Education>();
            ModelState.DebugErrors();
            return View("Education", vm);
        }

        var uCurrent = await GetCurrentUserAsync();
        if (uCurrent == null) return NotFound();

        var e = new Education
        {
            Id = Helper.GenerateId(_context.Educations, "E"),
            UserId = uCurrent.Id,
            Institution = vm.Institution,
            Qualification = vm.Institution
        };

        _context.Educations.Add(e);
        await _context.SaveChangesAsync();

        // Audit log for adding education
        var log = new AuditLog
        {
            UserId = uCurrent.Id,
            TableName = "Educations",
            ActionType = "Create",
            RecordId = e.Id,
            Changes = $"Added education: {e.Qualification} at {e.Institution}"
        };
        _context.AuditLogs.Add(log);
        await _context.SaveChangesAsync();

        return RedirectToAction(nameof(Education));
    }

    public async Task<IActionResult> DeleteEducation(string id)
    {
        var edu = await _context.Educations.FindAsync(id);
        if (edu != null)
        {
            _context.Educations.Remove(edu);
            await _context.SaveChangesAsync();

            // Audit log for deleting education
            var log = new AuditLog
            {
                UserId = edu.UserId,
                TableName = "Educations",
                ActionType = "Delete",
                RecordId = edu.Id,
                Changes = $"Deleted education: {edu.Qualification} at {edu.Institution}"
            };
            _context.AuditLogs.Add(log);
            await _context.SaveChangesAsync();
        }
        return RedirectToAction(nameof(Education));
    }


    // 返回匹配的Qualification
    [HttpGet]
    public IActionResult SearchQualification(string term)
    {
        if (string.IsNullOrWhiteSpace(term)) return Json(Enumerable.Empty<object>());
        var list = _context.Qualifications
            .Where(q => q.Name.Contains(term))
            .Select(q => new { id = q.Id, name = q.Name })
            .Take(10)
            .ToList();
        return Json(list);
    }

    // 返回匹配的Institution
    [HttpGet]
    public IActionResult SearchInstitution(string term)
    {
        if (string.IsNullOrWhiteSpace(term)) return Json(Enumerable.Empty<object>());
        var list = _context.Institutions
            .Where(i => i.Name.Contains(term))
            .Select(i => new { id = i.Id, name = i.Name })
            .Take(10)
            .ToList();
        return Json(list);
    }
}