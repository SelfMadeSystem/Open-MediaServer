﻿using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Cryptography.KeyDerivation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Open_MediaServer.Backend.Schema;
using Open_MediaServer.Database.Schema;
using SQLiteNetExtensionsAsync.Extensions;

namespace Open_MediaServer.Backend.Controllers;

[ApiController]
[Route("/api/[controller]")]
public class UserApiController : ControllerBase
{
    [HttpPost("/api/account/register/")]
    public async Task<ActionResult> PostRegister(UserSchema.User userRegister)
    {
        if (ModelState.IsValid)
        {
            var usernameExists = Program.Database.UserDatabase.Table<DatabaseSchema.User>().ToListAsync().Result
                .Any(user => user.Username == userRegister.Username);

            if (usernameExists)
            {
                return StatusCode(StatusCodes.Status403Forbidden, "Profile username already in use.");
            }

            byte[] salt = new byte[128 / 8];
            using var rng = RandomNumberGenerator.Create();
            rng.GetNonZeroBytes(salt);

            string hashedPassword = Convert.ToBase64String(KeyDerivation.Pbkdf2(
                password: userRegister.Password,
                salt: salt,
                prf: KeyDerivationPrf.HMACSHA256,
                iterationCount: 100000,
                numBytesRequested: 256 / 8));

            byte[] sessionKey = new byte[64];
            rng.GetNonZeroBytes(sessionKey);
            Response.Cookies.Append("user_session", Convert.ToBase64String(sessionKey), new CookieOptions()
            {
                IsEssential = true,
                Secure = true,
                SameSite = SameSiteMode.Lax
            });

            var userSchema = new DatabaseSchema.User()
            {
                Username = userRegister.Username,
                Password = hashedPassword,
                Salt = salt,
                SessionKey = Convert.ToBase64String(sessionKey),
                CreationDate = DateTime.UtcNow,
                Uploads = new()
            };

            Console.WriteLine("Inserting user into sqlite db");
            await Program.Database.UserDatabase.InsertWithChildrenAsync(userSchema);

            string serializedJson = JsonSerializer.Serialize(userSchema, new JsonSerializerOptions()
            {
                WriteIndented = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            });
            Console.WriteLine(serializedJson);
            return StatusCode(StatusCodes.Status200OK);
        }

        return StatusCode(StatusCodes.Status400BadRequest, ModelState);
    }

    [HttpPost("/api/account/login/")]
    public async Task<ActionResult> PostLogin(UserSchema.User userLogin)
    {
        if (ModelState.IsValid)
        {
            var user = await Program.Database.UserDatabase
                .FindAsync<DatabaseSchema.User>(user => user.Username == userLogin.Username);
            if (user == null)
            {
                return StatusCode(StatusCodes.Status400BadRequest,
                    "No user has a account associated with that username.");
            }

            string hashedPassword = Convert.ToBase64String(KeyDerivation.Pbkdf2(
                password: userLogin.Password,
                salt: user.Salt,
                prf: KeyDerivationPrf.HMACSHA256,
                iterationCount: 100000,
                numBytesRequested: 256 / 8));
            if (hashedPassword.SequenceEqual(user.Password))
            {
                if (user.SessionKey == null)
                {
                    byte[] sessionKey = new byte[64];
                    using var rng = RandomNumberGenerator.Create();
                    rng.GetNonZeroBytes(sessionKey);
                    Response.Cookies.Append("user_session", Convert.ToBase64String(sessionKey), new CookieOptions()
                    {
                        IsEssential = true,
                        Secure = true,
                        SameSite = SameSiteMode.Lax
                    });
                    user.SessionKey = Convert.ToBase64String(sessionKey);
                    await Program.Database.UserDatabase.UpdateWithChildrenAsync(user);

                    string serializedJson = JsonSerializer.Serialize(user, new JsonSerializerOptions()
                    {
                        WriteIndented = true,
                        ReadCommentHandling = JsonCommentHandling.Skip
                    });
                    Console.WriteLine(serializedJson);
                }

                if (Request.Cookies["user_session"] == null)
                {
                    Response.Cookies.Append("user_session", user.SessionKey, new CookieOptions()
                    {
                        IsEssential = true,
                        Secure = true,
                        SameSite = SameSiteMode.Lax
                    });
                }

                return StatusCode(StatusCodes.Status200OK);
            }

            return StatusCode(StatusCodes.Status401Unauthorized);
        }

        return StatusCode(StatusCodes.Status400BadRequest, ModelState);
    }

    [HttpPost("/api/account/delete/")]
    public async Task<ActionResult> PostDelete(UserSchema.UserDelete userDelete)
    {
        if (ModelState.IsValid)
        {
            var userWithoutChildren = await Program.Database.UserDatabase.FindAsync<DatabaseSchema.User>(user =>
                user.Username == userDelete.User.Username);
            if (userWithoutChildren == null)
            {
                return StatusCode(StatusCodes.Status400BadRequest, "Unable to find account associated.");
            }

            var user =
                await Program.Database.UserDatabase.FindWithChildrenAsync<DatabaseSchema.User>(userWithoutChildren.Id);

            if (user == null)
            {
                return StatusCode(StatusCodes.Status500InternalServerError);
            }

            string hashedPassword = Convert.ToBase64String(KeyDerivation.Pbkdf2(
                password: userDelete.User.Password,
                salt: user.Salt,
                prf: KeyDerivationPrf.HMACSHA256,
                iterationCount: 100000,
                numBytesRequested: 256 / 8));
            if (hashedPassword.SequenceEqual(user.Password))
            {
                if (userDelete.DeleteMedia)
                {
                    foreach (var mediaIdentity in user.Uploads)
                    {
                        await Program.Database.MediaDatabase.DeleteAsync<DatabaseSchema.Media>(mediaIdentity.Id);
                    }
                }

                await Program.Database.UserDatabase.DeleteAsync<DatabaseSchema.User>(user.Id);
                return StatusCode(StatusCodes.Status200OK);
            }
        }

        return StatusCode(StatusCodes.Status400BadRequest, ModelState);
    }
}