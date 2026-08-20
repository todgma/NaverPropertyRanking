const MEMBER_SHEET = '회원정보';
const HISTORY_SHEET = '이력';
const SESSION_SHEET = '접속현황';
const NOTICE_SHEET = '공지사항';
const MEMBER_GROUP_SHEET = '회원_단체';
const MEMBER_HEADERS = [
  '아이디', '비밀번호해시', '솔트', '이름', '가입일시',
  '멤버십 시작일자', '멤버십 종료일자', '사용가능PC수', '등급'
];
const HISTORY_HEADERS = ['토큰', '로그인일시', '아이디', '아이피'];
const SESSION_HEADERS = [
  '세션ID', '토큰', '아이디', 'PC명', '로그인일시',
  '마지막신호일시', '종료일시', '상태'
];
const NOTICE_HEADERS = ['공지내용'];
const MEMBER_GROUP_HEADERS = ['id', '단체id'];
const LEGACY_HASH_ITERATIONS = 8000;
const CURRENT_HASH_ITERATIONS = 500;
const CURRENT_HASH_PREFIX = 'v2$';
// 회원가입 시 멤버십 종료일자 기본값: 가입일 다음날(가입 당일 하루 사용 가능).
const DEFAULT_SIGNUP_MEMBERSHIP_DAYS = 1;
const DEFAULT_MEMBER_GRADE = 1;

function doPost(e) {
  try {
    if (!e || !e.postData || !e.postData.contents) {
      return json_({
        success: false,
        message: 'doPost는 편집기에서 직접 실행하지 말고 배포된 웹 앱 URL로 호출하세요. 최초 설정은 configure를 실행하세요.'
      });
    }
    const request = JSON.parse(e.postData.contents);
    const action = String(request.action || '').toLowerCase();
    if (action === 'signup') return json_(signUp_(request));
    if (action === 'login') return json_(login_(request));
    if (action === 'heartbeat') return json_(heartbeat_(request));
    if (action === 'logout') return json_(logout_(request));
    if (action === 'savemembergroup') return json_(saveMemberGroup_(request));
    return json_({ success: false, message: '지원하지 않는 요청입니다.' });
  } catch (error) {
    console.error(error && error.stack ? error.stack : error);
    return json_({ success: false, message: '서버 처리 중 오류가 발생했습니다.' });
  }
}

function doGet() {
  try {
    const sheets = ensureSheets_();
    return json_({
      success: true,
      message: 'NaverPropertyRanking 인증 서비스가 실행 중입니다.',
      noticeCount: getNotices_(sheets.notices).length
    });
  } catch (error) {
    return json_({ success: false, message: '인증 서비스 초기화에 실패했습니다.' });
  }
}

/**
 * 최초 한 번 Apps Script 편집기에서 실행합니다.
 * 아래 값을 실제 스프레드시트 ID와 기본 정책으로 바꾼 뒤 실행하세요.
 */
function configure() {
  const spreadsheetId = 'YOUR_SPREADSHEET_ID';
  const defaultMembershipDays = DEFAULT_SIGNUP_MEMBERSHIP_DAYS;
  const defaultAllowedPcCount = 1;
  const sessionTimeoutSeconds = 300;
  if (spreadsheetId === 'YOUR_SPREADSHEET_ID') {
    throw new Error('configure 함수의 spreadsheetId를 먼저 변경하세요.');
  }

  const properties = PropertiesService.getScriptProperties();
  properties.setProperties({
    SPREADSHEET_ID: spreadsheetId,
    DEFAULT_MEMBERSHIP_DAYS: String(defaultMembershipDays),
    DEFAULT_ALLOWED_PC_COUNT: String(defaultAllowedPcCount),
    SESSION_TIMEOUT_SECONDS: String(sessionTimeoutSeconds)
  });
  if (!properties.getProperty('TOKEN_SECRET')) {
    properties.setProperty('TOKEN_SECRET', Utilities.getUuid() + Utilities.getUuid());
  }
  ensureSheets_();
  ensureCleanupTrigger_();
}

function cleanupExpiredSessions() {
  const lock = LockService.getScriptLock();
  lock.waitLock(20000);
  try {
    const sheets = ensureSheets_();
    expireStaleSessions_(sheets.sessions, new Date());
  } finally {
    lock.releaseLock();
  }
}

function ensureCleanupTrigger_() {
  const exists = ScriptApp.getProjectTriggers()
    .some(trigger => trigger.getHandlerFunction() === 'cleanupExpiredSessions');
  if (!exists) {
    ScriptApp.newTrigger('cleanupExpiredSessions')
      .timeBased()
      .everyMinutes(5)
      .create();
  }
}

function signUp_(request) {
  const userId = String(request.userId || '').trim();
  const password = String(request.password || '');
  const name = String(request.name || '').trim();
  const validation = validateSignUp_(userId, password, name);
  if (validation) return { success: false, message: validation };

  const lock = LockService.getScriptLock();
  lock.waitLock(20000);
  try {
    const sheets = ensureSheets_();
    if (findMember_(sheets.members, userId)) {
      return { success: false, message: '이미 사용 중인 아이디입니다.' };
    }

    const properties = PropertiesService.getScriptProperties();
    // 가입 시 종료일자는 스크립트 속성(DEFAULT_MEMBERSHIP_DAYS) 값과 무관하게
    // 항상 가입일 다음날로 고정한다. 예: 2026-08-20 가입 → 종료일자 2026-08-21.
    const allowedPcCount = positiveInt_(properties.getProperty('DEFAULT_ALLOWED_PC_COUNT'), 1);
    const now = new Date();
    const membershipStart = startOfDay_(now);
    const membershipEnd = addDays_(membershipStart, DEFAULT_SIGNUP_MEMBERSHIP_DAYS);
    const salt = Utilities.getUuid().replace(/-/g, '') + Utilities.getUuid().replace(/-/g, '');
    const passwordHash = hashPasswordV2_(password, salt);
    sheets.members.appendRow([
      userId, passwordHash, salt, name, now, membershipStart, membershipEnd, allowedPcCount,
      DEFAULT_MEMBER_GRADE
    ]);
    const memberRow = sheets.members.getLastRow();
    sheets.members.getRange(memberRow, 5).setNumberFormat('yyyy-mm-dd hh:mm:ss');
    sheets.members.getRange(memberRow, 6, 1, 2).setNumberFormat('yyyy-mm-dd');
    return { success: true, message: '회원가입이 완료되었습니다.' };
  } finally {
    lock.releaseLock();
  }
}

function login_(request) {
  const userId = String(request.userId || '').trim();
  const password = String(request.password || '');
  const deviceId = String(request.deviceId || '').trim();
  const deviceName = String(request.deviceName || '').trim().substring(0, 100);
  const ip = String(request.ip || '확인불가').trim().substring(0, 64);
  if (!/^[A-Za-z0-9._-]{4,50}$/.test(userId) || password.length < 4 || !deviceId) {
    return { success: false, code: 'INVALID_CREDENTIALS', message: '아이디 또는 패스워드를 확인하세요.' };
  }

  const lock = LockService.getScriptLock();
  lock.waitLock(20000);
  try {
    const sheets = ensureSheets_();
    const member = findMember_(sheets.members, userId);
    const passwordVerification = member
      ? verifyPassword_(password, member.salt, member.passwordHash)
      : { valid: false, needsUpgrade: false };
    if (!member || !passwordVerification.valid) {
      return { success: false, code: 'INVALID_CREDENTIALS', message: '아이디 또는 패스워드가 올바르지 않습니다.' };
    }
    if (passwordVerification.needsUpgrade) {
      sheets.members.getRange(member.row, 2).setValue(hashPasswordV2_(password, member.salt));
    }

    const now = new Date();
    const start = asDate_(member.membershipStart);
    const end = asDate_(member.membershipEnd);
    if (!isMembershipActive_(now, start, end)) {
      return { success: false, code: 'MEMBERSHIP_EXPIRED', message: '멤버십 사용 기간이 아닙니다. 관리자에게 문의하세요.' };
    }

    expireStaleSessions_(sheets.sessions, now);
    const token = createDeviceToken_(userId, deviceId);
    const activeTokens = enforcePcLimit_(sheets.sessions, userId, member.allowedPcCount, now);
    if (!activeTokens.has(token) && activeTokens.size >= member.allowedPcCount) {
      return {
        success: false,
        code: 'PC_LIMIT',
        message: `사용 가능한 PC 수(${member.allowedPcCount}대)를 초과했습니다.`
      };
    }

    closeActiveTokenSessions_(sheets.sessions, userId, token, now, 'REPLACED');
    const sessionId = Utilities.getUuid();
    sheets.sessions.appendRow([
      sessionId, token, userId, deviceName || '알 수 없음', now, now, '', 'ACTIVE'
    ]);
    activeTokens.add(token);
    sheets.history.appendRow([token, now, userId, ip || '확인불가']);
    const notices = getNotices_(sheets.notices);
    return {
      success: true,
      code: 'LOGIN_SUCCESS',
      message: '로그인되었습니다.',
      userId: userId,
      name: member.name,
      token: token,
      sessionId: sessionId,
      membershipStart: start.toISOString(),
      membershipEnd: end.toISOString(),
      allowedPcCount: member.allowedPcCount,
      currentPcCount: activeTokens.size,
      grade: member.grade,
      notices: notices
    };
  } finally {
    lock.releaseLock();
  }
}

function heartbeat_(request) {
  const sessionId = String(request.sessionId || '').trim();
  const token = String(request.token || '').trim();
  if (!sessionId || !token) {
    return { success: false, code: 'INVALID_SESSION', message: '세션 정보가 없습니다.' };
  }

  const lock = LockService.getScriptLock();
  lock.waitLock(20000);
  try {
    const sheets = ensureSheets_();
    const now = new Date();
    expireStaleSessions_(sheets.sessions, now);
    let session = findSession_(sheets.sessions, sessionId);
    if (!session || session.token !== token || session.status !== 'ACTIVE') {
      return { success: false, code: 'SESSION_EXPIRED', message: '로그인 세션이 만료되었습니다. 다시 로그인하세요.' };
    }

    const member = findMember_(sheets.members, session.userId);
    if (!member) {
      closeSession_(sheets.sessions, session.row, now, 'MEMBER_DELETED');
      return { success: false, code: 'MEMBER_NOT_FOUND', message: '회원정보가 삭제되었습니다.' };
    }
    const start = asDate_(member.membershipStart);
    const end = asDate_(member.membershipEnd);
    if (!isMembershipActive_(now, start, end)) {
      closeSession_(sheets.sessions, session.row, now, 'MEMBERSHIP_EXPIRED');
      return { success: false, code: 'MEMBERSHIP_EXPIRED', message: '멤버십 사용 기간이 종료되었습니다.' };
    }

    const activeTokens = enforcePcLimit_(sheets.sessions, session.userId, member.allowedPcCount, now);
    session = findSession_(sheets.sessions, sessionId);
    if (!session || session.status !== 'ACTIVE' || !activeTokens.has(token)) {
      return { success: false, code: 'PC_LIMIT', message: `사용 가능한 PC 수(${member.allowedPcCount}대)를 초과했습니다.` };
    }

    sheets.sessions.getRange(session.row, 6).setValue(now);
    return {
      success: true,
      code: 'HEARTBEAT_OK',
      message: '접속 상태가 갱신되었습니다.',
      allowedPcCount: member.allowedPcCount,
      currentPcCount: activeTokens.size,
      grade: member.grade,
      membershipEnd: end.toISOString(),
      notices: getNotices_(sheets.notices)
    };
  } finally {
    lock.releaseLock();
  }
}

function logout_(request) {
  const sessionId = String(request.sessionId || '').trim();
  const token = String(request.token || '').trim();
  if (!sessionId || !token) {
    return { success: false, code: 'INVALID_SESSION', message: '세션 정보가 없습니다.' };
  }

  const lock = LockService.getScriptLock();
  lock.waitLock(20000);
  try {
    const sheets = ensureSheets_();
    const session = findSession_(sheets.sessions, sessionId);
    if (!session || session.token !== token) {
      return { success: true, code: 'ALREADY_CLOSED', message: '이미 종료된 세션입니다.' };
    }
    if (session.status === 'ACTIVE') closeSession_(sheets.sessions, session.row, new Date(), 'LOGOUT');
    return { success: true, code: 'LOGOUT_SUCCESS', message: '로그아웃되었습니다.' };
  } finally {
    lock.releaseLock();
  }
}

function saveMemberGroup_(request) {
  const sessionId = String(request.sessionId || '').trim();
  const token = String(request.token || '').trim();
  const groupId = String(request.groupId || '').trim();
  if (!sessionId || !token) {
    return { success: false, code: 'INVALID_SESSION', message: '세션 정보가 없습니다.' };
  }
  if (!groupId || groupId.length > 200 || /[\u0000-\u001F\u007F]/.test(groupId) || /^[=+\-@]/.test(groupId)) {
    return { success: false, code: 'INVALID_GROUP_ID', message: '단체 ID 형식을 확인하세요.' };
  }

  const lock = LockService.getScriptLock();
  lock.waitLock(20000);
  try {
    const sheets = ensureSheets_();
    const now = new Date();
    expireStaleSessions_(sheets.sessions, now);
    const session = findSession_(sheets.sessions, sessionId);
    if (!session || session.token !== token || session.status !== 'ACTIVE') {
      return { success: false, code: 'SESSION_EXPIRED', message: '로그인 세션이 만료되었습니다. 다시 로그인하세요.' };
    }
    if (findMemberGroup_(sheets.memberGroups, session.userId, groupId)) {
      return { success: true, code: 'MEMBER_GROUP_EXISTS', message: '이미 등록된 단체 ID입니다.' };
    }

    const nextRow = sheets.memberGroups.getLastRow() + 1;
    sheets.memberGroups
      .getRange(nextRow, 1, 1, MEMBER_GROUP_HEADERS.length)
      .setNumberFormat('@')
      .setValues([[session.userId, groupId]]);
    return { success: true, code: 'MEMBER_GROUP_ADDED', message: '조회한 단체 ID를 저장했습니다.' };
  } finally {
    lock.releaseLock();
  }
}

function ensureSheets_() {
  const spreadsheetId = PropertiesService.getScriptProperties().getProperty('SPREADSHEET_ID');
  if (!spreadsheetId) throw new Error('SPREADSHEET_ID Script Property가 없습니다. configure를 실행하세요.');
  const spreadsheet = SpreadsheetApp.openById(spreadsheetId);
  const allSheets = spreadsheet.getSheets();
  const sheetsByName = new Map(allSheets.map(sheet => [sheet.getName(), sheet]));
  const members = ensureSheet_(spreadsheet, sheetsByName, MEMBER_SHEET, MEMBER_HEADERS);
  ensureMemberGrades_(members);
  const history = ensureSheet_(spreadsheet, sheetsByName, HISTORY_SHEET, HISTORY_HEADERS);
  const sessions = ensureSheet_(spreadsheet, sheetsByName, SESSION_SHEET, SESSION_HEADERS);
  const memberGroups = ensureSheet_(spreadsheet, sheetsByName, MEMBER_GROUP_SHEET, MEMBER_GROUP_HEADERS);
  let notices = allSheets.filter(sheet => normalizeSheetName_(sheet.getName()) === normalizeSheetName_(NOTICE_SHEET));
  if (notices.length === 0) {
    notices = [ensureSheet_(spreadsheet, sheetsByName, NOTICE_SHEET, NOTICE_HEADERS)];
  }
  return {
    members: members,
    history: history,
    sessions: sessions,
    notices: notices,
    memberGroups: memberGroups
  };
}

function getNotices_(sheets) {
  const result = [];
  const seen = new Set();
  const candidates = Array.isArray(sheets) ? sheets : [sheets];
  candidates.forEach(sheet => {
    const lastRow = sheet.getLastRow();
    if (lastRow < 2) return;
    sheet
      .getRange(2, 1, lastRow - 1, 1)
      .getDisplayValues()
      .map(row => String(row[0] || '').trim())
      .filter(value => value.length > 0)
      .forEach(value => {
        const notice = value.substring(0, 500);
        if (seen.has(notice) || result.length >= 100) return;
        seen.add(notice);
        result.push(notice);
      });
  });
  return result;
}

function normalizeSheetName_(value) {
  return String(value || '').replace(/[\s\u200B-\u200D\uFEFF]/g, '').toLowerCase();
}

function ensureSheet_(spreadsheet, sheetsByName, name, headers) {
  let sheet = sheetsByName.get(name);
  if (!sheet) {
    sheet = spreadsheet.insertSheet(name);
    sheetsByName.set(name, sheet);
  }
  if (sheet.getLastRow() === 0) {
    sheet.getRange(1, 1, 1, headers.length).setValues([headers]);
    sheet.setFrozenRows(1);
    sheet.getRange(1, 1, 1, headers.length).setFontWeight('bold');
    sheet.autoResizeColumns(1, headers.length);
  } else {
    const currentHeaders = sheet
      .getRange(1, 1, 1, Math.max(sheet.getLastColumn(), headers.length))
      .getDisplayValues()[0];
    headers.forEach((header, index) => {
      if (!String(currentHeaders[index] || '').trim()) sheet.getRange(1, index + 1).setValue(header);
    });
  }
  return sheet;
}

function ensureMemberGrades_(sheet) {
  const lastRow = sheet.getLastRow();
  if (lastRow < 2) return;
  const gradeColumn = MEMBER_HEADERS.indexOf('등급') + 1;
  const range = sheet.getRange(2, gradeColumn, lastRow - 1, 1);
  const values = range.getValues();
  let changed = false;
  values.forEach(row => {
    if (positiveInt_(row[0], 0) > 0) return;
    row[0] = DEFAULT_MEMBER_GRADE;
    changed = true;
  });
  if (changed) range.setValues(values);
}

function findMember_(sheet, userId) {
  const lastRow = sheet.getLastRow();
  if (lastRow < 2) return null;
  const rows = sheet.getRange(2, 1, lastRow - 1, MEMBER_HEADERS.length).getValues();
  for (let index = 0; index < rows.length; index++) {
    if (String(rows[index][0]).trim().toLowerCase() !== userId.toLowerCase()) continue;
    return {
      row: index + 2,
      userId: String(rows[index][0]),
      passwordHash: String(rows[index][1]),
      salt: String(rows[index][2]),
      name: String(rows[index][3]),
      membershipStart: rows[index][5],
      membershipEnd: rows[index][6],
      allowedPcCount: positiveInt_(rows[index][7], 1),
      grade: positiveInt_(rows[index][8], DEFAULT_MEMBER_GRADE)
    };
  }
  return null;
}

function findSession_(sheet, sessionId) {
  const lastRow = sheet.getLastRow();
  if (lastRow < 2) return null;
  const rows = sheet.getRange(2, 1, lastRow - 1, SESSION_HEADERS.length).getValues();
  for (let index = 0; index < rows.length; index++) {
    if (String(rows[index][0]) !== sessionId) continue;
    return {
      row: index + 2,
      sessionId: String(rows[index][0]),
      token: String(rows[index][1]),
      userId: String(rows[index][2]),
      loginAt: rows[index][4],
      lastHeartbeatAt: rows[index][5],
      status: String(rows[index][7])
    };
  }
  return null;
}

function findMemberGroup_(sheet, userId, groupId) {
  const lastRow = sheet.getLastRow();
  if (lastRow < 2) return false;
  const normalizedUserId = userId.toLowerCase();
  const normalizedGroupId = groupId.toLowerCase();
  const rows = sheet.getRange(2, 1, lastRow - 1, MEMBER_GROUP_HEADERS.length).getDisplayValues();
  return rows.some(row =>
    String(row[0]).trim().toLowerCase() === normalizedUserId &&
    String(row[1]).trim().toLowerCase() === normalizedGroupId
  );
}

function getActiveSessions_(sheet, userId) {
  const result = [];
  const lastRow = sheet.getLastRow();
  if (lastRow < 2) return result;
  const rows = sheet.getRange(2, 1, lastRow - 1, SESSION_HEADERS.length).getValues();
  rows.forEach((row, index) => {
    if (String(row[2]).trim().toLowerCase() !== userId.toLowerCase()) return;
    if (String(row[7]) !== 'ACTIVE') return;
    result.push({ row: index + 2, token: String(row[1]) });
  });
  return result;
}

function expireStaleSessions_(sheet, now) {
  const timeoutSeconds = positiveInt_(
    PropertiesService.getScriptProperties().getProperty('SESSION_TIMEOUT_SECONDS'),
    300
  );
  const cutoff = now.getTime() - timeoutSeconds * 1000;
  const lastRow = sheet.getLastRow();
  if (lastRow < 2) return;
  const rows = sheet.getRange(2, 1, lastRow - 1, SESSION_HEADERS.length).getValues();
  rows.forEach((row, index) => {
    if (String(row[7]) !== 'ACTIVE') return;
    const lastHeartbeat = asDate_(row[5]);
    if (lastHeartbeat && lastHeartbeat.getTime() >= cutoff) return;
    closeSession_(sheet, index + 2, now, 'EXPIRED');
  });
}

function enforcePcLimit_(sheet, userId, allowedPcCount, now) {
  const allowedTokens = new Set();
  const sessions = getActiveSessions_(sheet, userId);
  sessions.forEach(session => {
    if (allowedTokens.has(session.token)) return;
    if (allowedTokens.size < allowedPcCount) {
      allowedTokens.add(session.token);
      return;
    }
    closeActiveTokenSessions_(sheet, userId, session.token, now, 'PC_LIMIT');
  });
  return allowedTokens;
}

function closeActiveTokenSessions_(sheet, userId, token, now, status) {
  getActiveSessions_(sheet, userId)
    .filter(session => session.token === token)
    .forEach(session => closeSession_(sheet, session.row, now, status));
}

function closeSession_(sheet, row, now, status) {
  sheet.getRange(row, 7, 1, 2).setValues([[now, status]]);
}

function createDeviceToken_(userId, deviceId) {
  const properties = PropertiesService.getScriptProperties();
  let secret = properties.getProperty('TOKEN_SECRET');
  if (!secret) {
    secret = Utilities.getUuid() + Utilities.getUuid();
    properties.setProperty('TOKEN_SECRET', secret);
  }
  const signature = Utilities.computeHmacSha256Signature(`${userId}:${deviceId}`, secret);
  return Utilities.base64EncodeWebSafe(signature).replace(/=+$/, '');
}

function hashPasswordIterations_(password, salt, iterations) {
  let value = `${salt}:${password}`;
  for (let index = 0; index < iterations; index++) {
    const digest = Utilities.computeDigest(
      Utilities.DigestAlgorithm.SHA_256,
      value,
      Utilities.Charset.UTF_8
    );
    value = Utilities.base64EncodeWebSafe(digest);
  }
  return value;
}

function hashPasswordV2_(password, salt) {
  return CURRENT_HASH_PREFIX + hashPasswordIterations_(password, salt, CURRENT_HASH_ITERATIONS);
}

function verifyPassword_(password, salt, storedHash) {
  if (storedHash.indexOf(CURRENT_HASH_PREFIX) === 0) {
    return {
      valid: constantTimeEquals_(storedHash, hashPasswordV2_(password, salt)),
      needsUpgrade: false
    };
  }
  return {
    valid: constantTimeEquals_(storedHash, hashPasswordIterations_(password, salt, LEGACY_HASH_ITERATIONS)),
    needsUpgrade: true
  };
}

function constantTimeEquals_(left, right) {
  if (left.length !== right.length) return false;
  let difference = 0;
  for (let index = 0; index < left.length; index++) {
    difference |= left.charCodeAt(index) ^ right.charCodeAt(index);
  }
  return difference === 0;
}

function validateSignUp_(userId, password, name) {
  if (!/^[A-Za-z0-9._-]{4,50}$/.test(userId)) {
    return '아이디는 영문·숫자·._- 조합으로 4~50자를 입력하세요.';
  }
  if (password.length < 4 || password.length > 100) return '패스워드는 4~100자로 입력하세요.';
  if (!name || name.length > 50 || /^[=+\-@]/.test(name)) return '이름을 확인하세요.';
  return null;
}

function positiveInt_(value, fallback) {
  const parsed = Number(value);
  return Number.isInteger(parsed) && parsed > 0 ? parsed : fallback;
}

function asDate_(value) {
  const date = value instanceof Date ? value : new Date(value);
  return isNaN(date.getTime()) ? null : date;
}

function startOfDay_(date) {
  const result = new Date(date.getTime());
  result.setHours(0, 0, 0, 0);
  return result;
}

function addDays_(date, days) {
  const result = new Date(date.getTime());
  result.setDate(result.getDate() + days);
  return result;
}

function isMembershipActive_(now, start, end) {
  if (!start || !end) return false;
  const today = startOfDay_(now);
  const startDate = startOfDay_(start);
  const endDate = startOfDay_(end);
  return today >= startDate && today < endDate;
}

function json_(value) {
  return ContentService
    .createTextOutput(JSON.stringify(value))
    .setMimeType(ContentService.MimeType.JSON);
}
