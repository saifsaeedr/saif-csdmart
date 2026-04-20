using System.Text.Json;
using Dmart.Api;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Models;

// Mirrors dmart's pytests/api_user_models_erros_test.py — error envelope shape.
public class ErrorTests
{
    [Fact]
    public void Invalid_Otp_Error_Has_Type_Code_Message()
    {
        var err = new Error(Type: "auth", Code: InternalErrorCode.OTP_INVALID, Message: "code mismatch", Info: null);
        err.Type.ShouldBe("auth");
        err.Code.ShouldBe(InternalErrorCode.OTP_INVALID);
        err.Message.ShouldBe("code mismatch");
        err.Info.ShouldBeNull();
    }

    [Fact]
    public void Expired_Otp_Error_Has_Expected_Properties()
    {
        var info = new List<Dictionary<string, object>>
        {
            new() { ["after_seconds"] = 600 },
        };
        var err = new Error("auth", InternalErrorCode.OTP_EXPIRED, "code expired", info);
        err.Code.ShouldBe(InternalErrorCode.OTP_EXPIRED);
        err.Info.ShouldNotBeNull();
        err.Info!.Count.ShouldBe(1);
        err.Info[0]["after_seconds"].ShouldBe(600);
    }

    [Fact]
    public void Response_Fail_Builds_Failed_Status_With_Error()
    {
        var resp = Response.Fail(InternalErrorCode.OTP_INVALID, "code mismatch", "auth");
        resp.Status.ShouldBe(Status.Failed);
        resp.Error.ShouldNotBeNull();
        resp.Error!.Code.ShouldBe(InternalErrorCode.OTP_INVALID);
        resp.Error.Type.ShouldBe("auth");
    }

    [Fact]
    public void Response_Fail_Accepts_Int_Code_Directly()
    {
        var resp = Response.Fail(InternalErrorCode.LOCKED_ENTRY, "already locked", "db");
        resp.Status.ShouldBe(Status.Failed);
        resp.Error!.Code.ShouldBe(InternalErrorCode.LOCKED_ENTRY);  // == 31
        resp.Error.Code.ShouldBe(31);
        resp.Error.Type.ShouldBe("db");
    }

    [Fact]
    public void Response_Ok_Serializes_Progress_Ticket_Shape()
    {
        // WorkflowService.ProgressAsync returns exactly this shape; user
        // reported the attributes silently disappeared from the JSON.
        var resp = Response.Ok(attributes: new()
        {
            ["state"] = "approved",
            ["is_open"] = true,
        });
        var json = JsonSerializer.Serialize(resp, DmartJsonContext.Default.Response);
        json.ShouldContain("\"attributes\"");
        json.ShouldContain("\"state\":\"approved\"");
        json.ShouldContain("\"is_open\":true");
    }

    [Fact]
    public void Response_Ok_Builds_Success_With_Records()
    {
        var resp = Response.Ok(records: new List<Record>(), attributes: new() { ["total"] = 0 });
        resp.Status.ShouldBe(Status.Success);
        resp.Error.ShouldBeNull();
        resp.Records.ShouldNotBeNull();
        resp.Attributes!["total"].ShouldBe(0);
    }

    // FailedResponseFilter HTTP status mapping
    [Theory]
    [InlineData(InternalErrorCode.NOT_ALLOWED, 401)]
    [InlineData(InternalErrorCode.NOT_AUTHENTICATED, 401)]
    [InlineData(InternalErrorCode.INVALID_TOKEN, 401)]
    [InlineData(InternalErrorCode.EXPIRED_TOKEN, 401)]
    [InlineData(InternalErrorCode.INVALID_USERNAME_AND_PASS, 401)]
    [InlineData(InternalErrorCode.USER_ACCOUNT_LOCKED, 401)]
    [InlineData(InternalErrorCode.SHORTNAME_DOES_NOT_EXIST, 404)]
    [InlineData(InternalErrorCode.OBJECT_NOT_FOUND, 404)]
    [InlineData(InternalErrorCode.CONFLICT, 409)]
    [InlineData(InternalErrorCode.SHORTNAME_ALREADY_EXIST, 409)]
    [InlineData(InternalErrorCode.LOCKED_ENTRY, 423)]
    [InlineData(InternalErrorCode.LOCK_UNAVAILABLE, 423)]
    [InlineData(InternalErrorCode.INVALID_DATA, 400)]
    [InlineData(InternalErrorCode.OTP_INVALID, 400)]
    [InlineData(InternalErrorCode.SOMETHING_WRONG, 400)]
    [InlineData(null, 400)]
    public void FailedResponseFilter_Maps_ErrorCode_To_HttpStatus(int? code, int expectedHttp)
    {
        FailedResponseFilter.MapErrorToHttpStatus(code).ShouldBe(expectedHttp);
    }
}
