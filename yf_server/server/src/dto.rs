use serde::{Deserialize, Serialize};

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LoginRequest {
    #[serde(default)]
    pub employee_no: String,
    pub password: String,
    /// M1 验证码：连续失败 3 次后由前端携带
    pub captcha_id: Option<String>,
    pub captcha_code: Option<String>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct UserBrief {
    pub id: u64,
    pub employee_no: String,
    pub real_name: String,
    pub user_type: String,
    pub supplier_id: Option<u64>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LoginResponse {
    pub access_token: String,
    pub expires_at: usize,
    pub must_change_password: bool,
    pub permissions: Vec<String>,
    /// 权限点中 type=MENU 的子集，前端据此渲染菜单与路由
    pub menus: Vec<String>,
    pub user: UserBrief,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TokenResponse {
    pub access_token: String,
    pub expires_at: usize,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ChangePasswordRequest {
    pub old_password: String,
    pub new_password: String,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ProfileResponse {
    pub user: UserBrief,
    /// 页面刷新后仍需保留强制改密状态，避免前端误判为普通登录态。
    pub must_change_password: bool,
    pub permissions: Vec<String>,
    pub menus: Vec<String>,
}

#[cfg(test)]
mod profile_response_tests {
    use super::{ProfileResponse, UserBrief};

    #[test]
    fn profile_serializes_must_change_password_in_camel_case() {
        let value = serde_json::to_value(ProfileResponse {
            user: UserBrief {
                id: 1,
                employee_no: "tester".into(),
                real_name: "Tester".into(),
                user_type: "INTERNAL".into(),
                supplier_id: None,
            },
            must_change_password: true,
            permissions: vec![],
            menus: vec![],
        })
        .unwrap();

        assert_eq!(value["mustChangePassword"], true);
    }
}

// ---------- 通用分页 ----------

/// 列表查询分页参数（各查询结构体内联使用；query string 反序列化不支持 flatten+数字，勿用 flatten）
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PageQuery {
    #[serde(default = "default_page")]
    pub page: u64,
    #[serde(default = "default_page_size")]
    pub page_size: u64,
}

pub fn default_page() -> u64 {
    1
}
pub fn default_page_size() -> u64 {
    20
}

impl PageQuery {
    /// (page, page_size)，page 从 1 开始，page_size ∈ [1,100]
    pub fn clamped(&self) -> (u64, u64) {
        clamp_page(self.page, self.page_size)
    }
}

pub fn clamp_page(page: u64, page_size: u64) -> (u64, u64) {
    let size = page_size.clamp(1, 100);
    // SeaORM computes OFFSET with u64 multiplication; bound untrusted page numbers.
    (page.clamp(1, u64::MAX / size), size)
}

#[cfg(test)]
mod pagination_tests {
    use super::clamp_page;

    #[test]
    fn extreme_pages_never_overflow_database_offsets() {
        for requested_size in [0, 1, 2, 20, 100, u64::MAX] {
            let (page, size) = clamp_page(u64::MAX, requested_size);
            assert!((page - 1).checked_mul(size).is_some());
            assert!(page.checked_mul(size).is_some());
        }
        assert_eq!(clamp_page(0, 0), (1, 1));
        assert_eq!(clamp_page(42, 20), (42, 20));
    }
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PageResp<T> {
    pub list: Vec<T>,
    pub total: u64,
    pub page: u64,
    pub page_size: u64,
}

impl<T> PageResp<T> {
    pub fn new(list: Vec<T>, total: u64, page: u64, page_size: u64) -> Self {
        let (page, page_size) = clamp_page(page, page_size);
        Self {
            list,
            total,
            page,
            page_size,
        }
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StatusReq {
    pub status: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PasswordReq {
    pub new_password: String,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CaptchaResp {
    pub captcha_id: String,
    pub svg: String,
}
