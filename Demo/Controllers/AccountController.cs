using Demo.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http;
using System.Net.Mail;
using System.Security.Claims;
using System.Text.Json;

public class AccountController : Controller
{
    private readonly DB db;
    private readonly Helper hp;
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration cf;

    public AccountController(DB context, Helper hp, IConfiguration config, IHttpClientFactory httpClientFactory, IConfiguration cf)
    {
        this.db = context;
        this.hp = hp;
        _config = config;
        _httpClientFactory = httpClientFactory;
        this.cf = cf;
    }

    #region Authentication

    // GET: Account/CheckEmail
    public bool CheckEmail(string email)
    {
        return !db.Users.Any(u => u.Email == email);
    }

    public IActionResult Register()
    {
        return View();
    }

    [HttpPost]
    public IActionResult Register(RegisterVM vm)
    {
        if (ModelState.IsValid("Email") &&
            db.Users.Any(u => u.Email == vm.Email))
        {
            ModelState.AddModelError("Email", "Duplicated Email.");
        }

        if (ModelState.IsValid("Photo"))
        {
            var err = hp.ValidatePhoto(vm.Photo);
            if (err != "") ModelState.AddModelError("Photo", err);
        }

        if (ModelState.IsValid)
        {
            // Insert member
            // TODO
            db.Users.Add(new User
            {
                Id = GenerateUserId(),
                Name = GenerateUsername(vm.Email),
                PasswordHash = hp.HashPassword(vm.Password),
                Email = vm.Email,
                PhoneNumber = "",
                Role = Role.JobSeeker,
            });

            db.SaveChanges();

            TempData["Info"] = "Register successfully. Please login.";
            return RedirectToAction("Login");
        }
        else
        {
            DebugModelStateErrors();
        }

        return View(vm);
    }

    public IActionResult Login(string? returnURL)
    {
        if (User.Identity != null && User.Identity.IsAuthenticated)
        {
            return RedirectToAction("Index", "Home");
        }
        ViewBag.ReturnURL = returnURL;
        return View();
    }

    [HttpPost]
    public IActionResult Login(LoginVM vm, string? returnURL)
    {
        var user = db.Users.Where(u => u.Email == vm.Email).FirstOrDefault();

        if (user == null)
        {
            ModelState.AddModelError("Email", "This email is not registered.");
            return View(vm);
        }
        if (user.Role != Role.JobSeeker)
        {
            ModelState.AddModelError("Email", "This email is already registered as " + user.Role.ToString());
            return View(vm);
        }
        if (!hp.VerifyPassword(user.PasswordHash, vm.Password))
        {
            ModelState.AddModelError("Password", "Invalid password.");
            return View(vm);
        }
        else if (ModelState.IsValid)
        {
            TempData["Info"] = "Login Successfully.";

            //hp.SignIn(user!.Email, user.Role.ToString(), vm.RememberMe);
            hp.SignIn(user, vm.RememberMe);

            if (!string.IsNullOrEmpty(returnURL))
                return Redirect(returnURL);

            return user.Role == Role.Employer
                ? RedirectToAction("Index", "Employer")
                : RedirectToAction("Index", "Profile");

        }
        return View(vm);
    }

    public IActionResult Logout(string? returnURL)
    {
        TempData["Info"] = "Logout Successfully.";

        hp.SignOut();

        return RedirectToAction("Index", "Home");
    }

    public IActionResult AccessDenied(string returnUrl = "")
    {
        if (!string.IsNullOrEmpty(returnUrl))
        {
            if (returnUrl.Contains("/Employer", StringComparison.OrdinalIgnoreCase))
            {
                ViewBag.Message = "This page requires Employer permission, please switch to your employer account.";
            }
            else if (returnUrl.Contains("/Admin", StringComparison.OrdinalIgnoreCase))
            {
                ViewBag.Message = "This page requires Admin permissions, please switch to the administrator account.";
            }
            else
            {
                ViewBag.Message = "You do not have permission to access this page.";
            }
        }
        else
        {
            ViewBag.Message = "You do not have permission to access this page.";
        }

        return View();
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



    private string GenerateUserId()
    {
        // 查询当前已有的最大编号
        var lastUser = db.Users
            .Where(u => u.Id.StartsWith("U"))
            .OrderByDescending(u => u.Id)
            .FirstOrDefault();

        int nextNumber = 1;
        if (lastUser != null)
        {
            string lastNumberStr = lastUser.Id.Substring(1);
            if (int.TryParse(lastNumberStr, out int lastNumber))
            {
                nextNumber = lastNumber + 1;
            }
        }

        return $"U{nextNumber.ToString("D3")}";  // 例如 JOB001、JOB002
    }

    private string GenerateUsername(string userEmail)
    {
        if (string.IsNullOrEmpty(userEmail))
            return "";

        int atIndex = userEmail.IndexOf('@');
        if (atIndex > 0)
            return userEmail.Substring(0, atIndex);

        return userEmail; // 如果没有@就原样返回
    }

    public IActionResult CheckUserId(string id)
    {
        bool exists = db.Users.Any(u => u.Id == id);
        if (exists)
            return Json($"ID {id} already exists");
        return Json(true);
    }

    // Google OAuth Login
    public IActionResult GoogleLogin()
    {
        var clientId = _config["Authentication:Google:ClientId"];
        var redirectUri = Url.Action("GoogleCallback", "Account", null, "http"); // 本地调试用 http

        var url = $"https://accounts.google.com/o/oauth2/v2/auth?" +
                  $"client_id={clientId}" +
                  $"&redirect_uri={redirectUri}" +
                  $"&response_type=code" +
                  $"&scope=openid%20email%20profile";

        return Redirect(url);
    }

    // Google OAuth Callback
    public async Task<IActionResult> GoogleCallback(string code)
    {
        if (string.IsNullOrEmpty(code))
        {
            TempData["Error"] = "Google login failed: No authorization code.";
            return RedirectToAction("Login");
        }

        var clientId = _config["Authentication:Google:ClientId"];
        var clientSecret = _config["Authentication:Google:ClientSecret"];
        var redirectUri = Url.Action("GoogleCallback", "Account", null, "https"); // 或 "https"

        var httpClient = _httpClientFactory.CreateClient();

        // Step 3: 用 code 换 token
        var tokenResponse = await httpClient.PostAsync("https://oauth2.googleapis.com/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
            {"code", code},
            {"client_id", clientId},
            {"client_secret", clientSecret},
            {"redirect_uri", redirectUri},
            {"grant_type", "authorization_code"}
            }));

        var responseContent = await tokenResponse.Content.ReadAsStringAsync();

        if (!tokenResponse.IsSuccessStatusCode)
        {
            TempData["Error"] = "Google token exchange failed: " + responseContent;
            return RedirectToAction("Login");
        }

        using var doc = JsonDocument.Parse(responseContent);

        string? idToken = doc.RootElement.TryGetProperty("id_token", out var idTokenElement)
            ? idTokenElement.GetString()
            : null;

        string? accessToken = doc.RootElement.TryGetProperty("access_token", out var accessTokenElement)
            ? accessTokenElement.GetString()
            : null;

        string? email = null;
        string? name = null;

        // Step 4A: 解析 id_token (JWT)
        if (!string.IsNullOrEmpty(idToken))
        {
            var handler = new JwtSecurityTokenHandler();
            var jwt = handler.ReadJwtToken(idToken);

            email = jwt.Claims.FirstOrDefault(c => c.Type == "email")?.Value;
            name = jwt.Claims.FirstOrDefault(c => c.Type == "name")?.Value;
        }

        // Step 4B: 如果 id_token 没有返回 email, 用 access_token 获取 profile
        if (string.IsNullOrEmpty(email) && !string.IsNullOrEmpty(accessToken))
        {
            var profileResponse = await httpClient.GetAsync(
                "https://www.googleapis.com/oauth2/v2/userinfo?access_token=" + accessToken);

            if (profileResponse.IsSuccessStatusCode)
            {
                var profileContent = await profileResponse.Content.ReadAsStringAsync();
                using var profileDoc = JsonDocument.Parse(profileContent);

                email = profileDoc.RootElement.TryGetProperty("email", out var emailElement)
                    ? emailElement.GetString()
                    : null;

                name = profileDoc.RootElement.TryGetProperty("name", out var nameElement)
                    ? nameElement.GetString()
                    : null;
            }
        }

        if (string.IsNullOrEmpty(email))
        {
            TempData["Error"] = "Google login failed: No email found.";
            return RedirectToAction("Login");
        }

        // Step 5: 查数据库 / 自动注册
        var user = db.Users.FirstOrDefault(u => u.Email == email);
        if (user == null)
        {
            user = new User
            {
                Id = GenerateUserId(),
                Name = name ?? GenerateUsername(email),
                Email = email,
                PhoneNumber = "",
                PasswordHash = "[Google]", // 区分普通用户
                Role = Role.JobSeeker
            };
            db.Users.Add(user);
            db.SaveChanges();
        }

        // Step 6: 登录用户 (写入 Cookie)
        var claims = new List<Claim>
    {
        new Claim(ClaimTypes.NameIdentifier, user.Id),
        new Claim(ClaimTypes.Name, user.Email ?? ""),
        new Claim(ClaimTypes.Email, user.Email ?? ""),
        new Claim(ClaimTypes.Role, user.Role.ToString())
    };

        var claimsIdentity = new ClaimsIdentity(
            claims, CookieAuthenticationDefaults.AuthenticationScheme);

        var authProperties = new AuthenticationProperties
        {
            IsPersistent = true, // 相当于 rememberMe
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(2)
        };

        await HttpContext.SignInAsync(
        "DefaultCookie", new ClaimsPrincipal(claimsIdentity), authProperties);

        TempData["Info"] = "Google Login Successful!";
        return RedirectToAction("Index", "Profile");
    }

    // GET: /Account/Settings
    [Authorize] // 必须登录才可以进
    public IActionResult Settings()
    {
        var email = User.Identity.Name;
        var user = db.Users.FirstOrDefault(u => u.Email == email);

        if (user == null) return RedirectToAction("Login");

        var model = new UpdateAccountViewModel
        {
            Email = user.Email
        };

        return View(model);
    }

    // POST: /Account/UpdateSettings
    [HttpPost]
    [Authorize]
    public IActionResult UpdateSettings(UpdateAccountViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View("Settings", model);
        }

        var email = User.Identity.Name;
        var user = db.Users.FirstOrDefault(u => u.Email == email);

        if (user == null) return RedirectToAction("Login");

        // 更新 Email
        user.Email = model.Email;

        // 更新密码（用 Identity 的 PasswordHasher）
        if (!string.IsNullOrEmpty(model.Password))
        {
            var ph = new PasswordHasher<User>();
            user.PasswordHash = ph.HashPassword(user, model.Password);
        }

        db.SaveChanges();

        TempData["Message"] = "Account updated successfully!";
        return RedirectToAction("Settings");
    }

    #endregion

    #region Forgot Password & Reset Password & Change Email

    // ================== 忘记密码 ==================
    [HttpGet]
    public IActionResult ForgotPassword() => View();

    [HttpPost]
    public IActionResult ForgotPassword(ForgotPasswordVM vm)
    {
        var userId = User.GetUserId();
        var user = db.Users.Find(userId);
        if (user == null || user.Email != vm.Email)
        {
            ModelState.AddModelError("Email", "Email is not exits or Email is not valid");
            return View(vm);
        }

        // 生成 token
        user.ResetToken = Guid.NewGuid().ToString("N");
        user.ResetTokenExpire = DateTime.Now.AddMinutes(30);
        db.SaveChanges();

        // 发送邮件
        var link = Url.Action("ResetPassword", "Account", new { token = user.ResetToken }, Request.Scheme);
        var mail = new MailMessage
        {
            Subject = "RESET PASSWORD",
            Body = $"Click the link to reset password: {link}",
            IsBodyHtml = true
        };
        mail.To.Add(user.Email);
        hp.SendEmail(mail);

        ViewBag.Msg = "Reset link is sent, please check it.";

        // TODO
        return View();
    }

    // ================== 重置密码 ==================
    [HttpGet]
    public IActionResult ResetPassword(string token)
    {
        var user = db.Users.FirstOrDefault(u => u.ResetToken == token && u.ResetTokenExpire > DateTime.Now);
        if (user == null) return Content("Invalid or expired link");

        return View(new ResetPasswordVM { Token = token });
    }

    [HttpPost]
    public IActionResult ResetPassword(ResetPasswordVM vm)
    {
        var user = db.Users.FirstOrDefault(u => u.ResetToken == vm.Token && u.ResetTokenExpire > DateTime.Now);
        if (user == null) return Content("Invalid or expired link");

        user.PasswordHash = hp.HashPassword(vm.NewPassword);
        user.ResetToken = null;
        user.ResetTokenExpire = null;
        db.SaveChanges();

        return RedirectToAction("Login");
    }

    // ================== 修改邮箱 ==================
    [HttpGet]
    public IActionResult ChangeEmail() => View();

    [HttpPost]
    public IActionResult ChangeEmail(ChangeEmailVM vm)
    {
        var userId = User.GetUserId();
        var user = db.Users.Find(userId);
        if (user == null) return Unauthorized();

        if (db.Users.Any(u => u.Email == vm.NewEmail))
        {
            ModelState.AddModelError("NewEmail", "User has already register");
            return View(vm);
        }

        if (!hp.VerifyPassword(user.PasswordHash, vm.Password))
        {
            ModelState.AddModelError("Password", "Password error");
            return View(vm);
        }

        // 保存待修改邮箱，并发验证链接
        user.PendingEmail = vm.NewEmail;
        user.ResetToken = Guid.NewGuid().ToString("N");
        user.ResetTokenExpire = DateTime.Now.AddMinutes(30);
        db.SaveChanges();

        var link = Url.Action("ConfirmEmailChange", "Account", new { token = user.ResetToken }, Request.Scheme);
        var mail = new MailMessage
        {
            Subject = "Confirm edit email",
            Body = $"Click to confirm your new email: {link}",
            IsBodyHtml = true
        };
        mail.To.Add(vm.NewEmail);
        hp.SendEmail(mail);

        ViewBag.Msg = "Confirm the new email is already done，please check.";
        return View();
    }

    [HttpGet]
    public IActionResult ConfirmEmailChange(string token)
    {
        var user = db.Users.FirstOrDefault(u => u.ResetToken == token && u.ResetTokenExpire > DateTime.Now);
        if (user == null) return Content("Invalid or expired link");

        if (!string.IsNullOrEmpty(user.PendingEmail))
        {
            user.Email = user.PendingEmail;
            user.PendingEmail = null;
        }
        user.ResetToken = null;
        user.ResetTokenExpire = null;
        db.SaveChanges();

        return RedirectToAction("Index", "Profile");
    }

    #endregion
}
