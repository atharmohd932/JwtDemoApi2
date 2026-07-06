using JwtDemoApi.Data;
using JwtDemoApi.Models;
using JwtDemoApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JwtDemoApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly TokenService _tokenService;
        private readonly IConfiguration _config;

        public AuthController(AppDbContext db, TokenService tokenService, IConfiguration config)
        {
            _db = db;
            _tokenService = tokenService;
            _config = config;
        }

        [HttpPost("register")]
        public async Task<IActionResult> Register(RegisterRequest request)
        {
            if (await _db.Users.AnyAsync(u => u.Username == request.Username))
                return BadRequest("Username already taken");

            var user = new User
            {
                Username = request.Username,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
                Role = "User" // first-class users are "User"; promote to "Admin" manually in the DB if needed
            };

            _db.Users.Add(user);
            await _db.SaveChangesAsync();
            return Ok("Registered successfully");
        }

        [HttpPost("login")]
        public async Task<ActionResult<TokenResponse>> Login(LoginRequest request)
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == request.Username);
            if (user is null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
                return Unauthorized("Invalid credentials");

            var accessToken = _tokenService.GenerateAccessToken(user);
            var refreshToken = _tokenService.GenerateRefreshToken();

            user.RefreshToken = refreshToken;
            user.RefreshTokenExpiry = DateTime.UtcNow.AddDays(double.Parse(_config["Jwt:RefreshTokenExpiryDays"]!));
            await _db.SaveChangesAsync();

            return Ok(new TokenResponse { AccessToken = accessToken, RefreshToken = refreshToken });
        }

        [HttpPost("refresh")]
        public async Task<ActionResult<TokenResponse>> Refresh(RefreshRequest request)
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.RefreshToken == request.RefreshToken);

            if (user is null || user.RefreshTokenExpiry < DateTime.UtcNow)
                return Unauthorized("Invalid or expired refresh token");

            var newAccessToken = _tokenService.GenerateAccessToken(user);
            var newRefreshToken = _tokenService.GenerateRefreshToken();

            // Rotate the refresh token so an old leaked one becomes useless
            user.RefreshToken = newRefreshToken;
            user.RefreshTokenExpiry = DateTime.UtcNow.AddDays(double.Parse(_config["Jwt:RefreshTokenExpiryDays"]!));
            await _db.SaveChangesAsync();

            return Ok(new TokenResponse { AccessToken = newAccessToken, RefreshToken = newRefreshToken });
        }

        [HttpPost("revoke")]
        [Microsoft.AspNetCore.Authorization.Authorize]
        public async Task<IActionResult> Revoke()
        {
            var username = User.Identity?.Name;
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username);
            if (user is null) return NotFound();

            user.RefreshToken = null;
            user.RefreshTokenExpiry = null;
            await _db.SaveChangesAsync();
            return Ok("Refresh token revoked (effectively logs the user out of refresh capability)");
        }
    }
}
