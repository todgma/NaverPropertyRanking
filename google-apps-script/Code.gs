const MEMBER_SHEET = '회원정보';
const HISTORY_SHEET = '이력';
const NOTICE_SHEET = '공지사항';
const MEMBER_GROUP_SHEET = '회원_단체';
const MEMBER_HEADERS = [
  '아이디', '비밀번호해시', '솔트', '이름', '가입일시',
  '멤버십 시작일자', '멤버십 종료일자', '사용가능PC수', '등급'
];
// 접속 상태를 토큰(계정×PC 조합) 기준 1행으로 관리한다.
// 로그인은 upsert, 하트비트는 최종접속일시만 갱신하므로 행이 누적되지 않는다.
// 최종로그인일시(실제 로그인)와 최종접속일시(하트비트)는 반드시 분리해서 관리한다.
const HISTORY_HEADERS = [
  '토큰', '아이디', 'PC명', '아이피', '앱버전', '세션ID',
  '최초로그인일시', '최종로그인일시', '최종접속일시', '종료일시', '상태'
];
// 시트 컬럼 번호(1부터). 배열 인덱스로 쓸 때는 -1 한다.
const HISTORY_COLUMN = {
  TOKEN: 1,
  USER_ID: 2,
  DEVICE_NAME: 3,
  IP: 4,
  APP_VERSION: 5,
  SESSION_ID: 6,
  FIRST_LOGIN_AT: 7,
  LAST_LOGIN_AT: 8,
  LAST_SEEN_AT: 9,
  CLOSED_AT: 10,
  STATUS: 11
};
const NOTICE_HEADERS = ['공지내용'];
const MEMBER_GROUP_HEADERS = ['id', '단체id'];
const LEGACY_HASH_ITERATIONS = 8000;
const CURRENT_HASH_ITERATIONS = 500;
const CURRENT_HASH_PREFIX = 'v2$';
// 회원가입 시 멤버십 종료일자 기본값: 가입일 다음날(가입 당일 하루 사용 가능).
const DEFAULT_SIGNUP_MEMBERSHIP_DAYS = 1;
const DEFAULT_MEMBER_GRADE = 1;
// 네이버 인증값을 담는 스크립트 속성 이름.
// 앱에 넣지 않고 로그인·접속 확인 응답으로 내려보내므로, 값이 새면 여기만 바꾸면 된다.
// 프로젝트 설정 > 스크립트 속성에서 관리한다(스프레드시트나 소스에 남기지 않는다).
const NAVER_CREDENTIAL_PROPERTIES = {
  AUTHORIZATION: 'NAVER_AUTHORIZATION',
  COOKIE: 'NAVER_COOKIE',
  FIN_COOKIE: 'NAVER_FIN_COOKIE'
};

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
  if (spreadsheetId === 'YOUR_SPREADSHEET_ID') {
    throw new Error('configure 함수의 spreadsheetId를 먼저 변경하세요.');
  }

  const properties = PropertiesService.getScriptProperties();
  properties.setProperties({
    SPREADSHEET_ID: spreadsheetId,
    DEFAULT_MEMBERSHIP_DAYS: String(defaultMembershipDays),
    DEFAULT_ALLOWED_PC_COUNT: String(defaultAllowedPcCount)
  });
  if (!properties.getProperty('TOKEN_SECRET')) {
    properties.setProperty('TOKEN_SECRET', Utilities.getUuid() + Utilities.getUuid());
  }
  ensureSheets_();
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
  const appVersion = String(request.appVersion || '').trim().substring(0, 40);
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

    // 이력 시트는 이 회원이 등록한 PC 목록이다. 등록된 PC에서 다시 로그인하는 것은
    // 언제든 허용하고, 새 PC는 사용가능PC수 안에서만 등록한다.
    // PC를 교체하면 관리자가 시트에서 이전 PC 행을 지워 자리를 비운다.
    const token = createDeviceToken_(userId, deviceId);
    const registeredPcs = getRegisteredPcs_(sheets.history, userId);
    const isRegisteredPc = registeredPcs.some(pc => pc.token === token);
    if (!isRegisteredPc && registeredPcs.length >= member.allowedPcCount) {
      return {
        success: false,
        code: 'PC_LIMIT',
        message: `사용 가능한 PC 수(${member.allowedPcCount}대)를 초과했습니다. ` +
          'PC를 교체하셨다면 관리자에게 이전 PC 정보 삭제를 요청하세요.'
      };
    }

    // 토큰(계정×PC)당 1행만 유지한다. 같은 PC에서 다시 로그인하면 기존 행을 갱신하면서
    // 세션ID가 새로 발급되므로 이전 세션은 자동으로 무효가 된다.
    const sessionId = Utilities.getUuid();
    upsertHistoryOnLogin_(sheets.history, {
      token: token,
      userId: userId,
      deviceName: deviceName || '알 수 없음',
      ip: ip || '확인불가',
      appVersion: appVersion,
      sessionId: sessionId,
      now: now
    });
    const currentPcCount = isRegisteredPc ? registeredPcs.length : registeredPcs.length + 1;
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
      currentPcCount: currentPcCount,
      grade: member.grade,
      notices: notices,
      naverCredentials: getNaverCredentials_()
    };
  } finally {
    lock.releaseLock();
  }
}

function heartbeat_(request) {
  const sessionId = String(request.sessionId || '').trim();
  const token = String(request.token || '').trim();
  const appVersion = String(request.appVersion || '').trim().substring(0, 40);
  if (!sessionId || !token) {
    return { success: false, code: 'INVALID_SESSION', message: '세션 정보가 없습니다.' };
  }

  const lock = LockService.getScriptLock();
  lock.waitLock(20000);
  try {
    const sheets = ensureSheets_();
    const now = new Date();
    // 시간 경과로 세션을 끊지 않는다. 등록된 PC 행이 남아 있고 세션ID가 맞으면 계속 유효하다.
    // 관리자가 시트에서 PC 행을 지우면 그 PC의 앱은 다음 확인 때 종료된다.
    const session = findSessionByToken_(sheets.history, token);
    if (!session || session.sessionId !== sessionId) {
      return { success: false, code: 'SESSION_EXPIRED', message: '로그인 세션이 만료되었습니다. 다시 로그인하세요.' };
    }

    const member = findMember_(sheets.members, session.userId);
    if (!member) {
      closeSession_(sheets.history, session.row, now, 'MEMBER_DELETED');
      return { success: false, code: 'MEMBER_NOT_FOUND', message: '회원정보가 삭제되었습니다.' };
    }
    const start = asDate_(member.membershipStart);
    const end = asDate_(member.membershipEnd);
    if (!isMembershipActive_(now, start, end)) {
      closeSession_(sheets.history, session.row, now, 'MEMBERSHIP_EXPIRED');
      return { success: false, code: 'MEMBERSHIP_EXPIRED', message: '멤버십 사용 기간이 종료되었습니다.' };
    }

    // 최종접속일시만 갱신한다. 최종로그인일시는 로그인 때만 기록해 구분을 유지한다.
    sheets.history.getRange(session.row, HISTORY_COLUMN.LAST_SEEN_AT).setValue(now);
    if (session.status !== 'ACTIVE') {
      sheets.history.getRange(session.row, HISTORY_COLUMN.CLOSED_AT, 1, 2).setValues([['', 'ACTIVE']]);
    }
    if (appVersion) {
      sheets.history.getRange(session.row, HISTORY_COLUMN.APP_VERSION).setValue(appVersion);
    }
    return {
      success: true,
      code: 'HEARTBEAT_OK',
      message: '접속 상태가 갱신되었습니다.',
      allowedPcCount: member.allowedPcCount,
      currentPcCount: getRegisteredPcs_(sheets.history, session.userId).length,
      grade: member.grade,
      membershipEnd: end.toISOString(),
      notices: getNotices_(sheets.notices),
      naverCredentials: getNaverCredentials_()
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
    const session = findSessionByToken_(sheets.history, token);
    if (!session || session.sessionId !== sessionId) {
      return { success: true, code: 'ALREADY_CLOSED', message: '이미 종료된 세션입니다.' };
    }
    if (session.status === 'ACTIVE') closeSession_(sheets.history, session.row, new Date(), 'LOGOUT');
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
    const session = findSessionByToken_(sheets.history, token);
    if (!session || session.sessionId !== sessionId) {
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
  const memberGroups = ensureSheet_(spreadsheet, sheetsByName, MEMBER_GROUP_SHEET, MEMBER_GROUP_HEADERS);
  let notices = allSheets.filter(sheet => normalizeSheetName_(sheet.getName()) === normalizeSheetName_(NOTICE_SHEET));
  if (notices.length === 0) {
    notices = [ensureSheet_(spreadsheet, sheetsByName, NOTICE_SHEET, NOTICE_HEADERS)];
  }
  return {
    members: members,
    history: history,
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

/** 이력 시트 전체를 한 번만 읽어 다루기 쉬운 형태로 돌려준다. */
function readHistoryRows_(sheet) {
  const lastRow = sheet.getLastRow();
  if (lastRow < 2) return [];
  return sheet
    .getRange(2, 1, lastRow - 1, HISTORY_HEADERS.length)
    .getValues()
    .map((row, index) => ({
      row: index + 2,
      token: String(row[HISTORY_COLUMN.TOKEN - 1]).trim(),
      userId: String(row[HISTORY_COLUMN.USER_ID - 1]).trim(),
      sessionId: String(row[HISTORY_COLUMN.SESSION_ID - 1]).trim(),
      firstLoginAt: row[HISTORY_COLUMN.FIRST_LOGIN_AT - 1],
      lastLoginAt: row[HISTORY_COLUMN.LAST_LOGIN_AT - 1],
      lastSeenAt: row[HISTORY_COLUMN.LAST_SEEN_AT - 1],
      status: String(row[HISTORY_COLUMN.STATUS - 1]).trim()
    }));
}

/**
 * 앱이 네이버 API를 호출할 때 쓸 인증값을 스크립트 속성에서 읽어 돌려준다.
 * 앱에는 저장하지 않고 메모리에서만 사용하므로, 배포된 exe를 뜯어도 값이 나오지 않는다.
 * 값이 유출되면 스크립트 속성만 바꾸면 모든 PC에 반영된다.
 */
function getNaverCredentials_() {
  const properties = PropertiesService.getScriptProperties();
  const authorization = String(properties.getProperty(NAVER_CREDENTIAL_PROPERTIES.AUTHORIZATION) || '').trim();
  const cookie = String(properties.getProperty(NAVER_CREDENTIAL_PROPERTIES.COOKIE) || '').trim();
  const finCookie = String(properties.getProperty(NAVER_CREDENTIAL_PROPERTIES.FIN_COOKIE) || '').trim();
  if (!authorization && !cookie && !finCookie) return null;
  return {
    authorization: authorization,
    cookie: cookie,
    finCookie: finCookie || cookie
  };
}

/** 토큰은 계정×PC마다 유일하므로 이력 시트의 기본 키로 쓴다. */
function findSessionByToken_(sheet, token) {
  const rows = readHistoryRows_(sheet);
  for (let index = 0; index < rows.length; index++) {
    if (rows[index].token === token) return rows[index];
  }
  return null;
}

/**
 * 로그인 시 토큰 행을 갱신하거나 새로 추가한다.
 * 기존 행이 있으면 최초로그인일시만 보존하고 나머지를 새 접속 정보로 덮어쓴다.
 */
function upsertHistoryOnLogin_(sheet, entry) {
  const existing = findSessionByToken_(sheet, entry.token);
  const values = [[
    entry.token,
    entry.userId,
    entry.deviceName,
    entry.ip,
    entry.appVersion,
    entry.sessionId,
    existing && existing.firstLoginAt ? existing.firstLoginAt : entry.now,
    entry.now,
    entry.now,
    '',
    'ACTIVE'
  ]];
  const row = existing ? existing.row : sheet.getLastRow() + 1;
  sheet.getRange(row, 1, 1, HISTORY_HEADERS.length).setValues(values);
  sheet
    .getRange(row, HISTORY_COLUMN.FIRST_LOGIN_AT, 1, 3)
    .setNumberFormat('yyyy-mm-dd hh:mm:ss');
  return row;
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

/**
 * 아이디 기준으로 등록된 PC 목록을 돌려준다.
 * 상태와 무관하게 행이 존재하면 그 PC는 자리를 차지한 것으로 본다.
 * PC를 교체하면 관리자가 시트에서 이전 PC 행을 삭제해 자리를 비운다.
 */
function getRegisteredPcs_(sheet, userId) {
  const normalizedUserId = userId.toLowerCase();
  return readHistoryRows_(sheet)
    .filter(row => row.token && row.userId.toLowerCase() === normalizedUserId);
}

/** 접속을 종료 처리한다. 행은 지우지 않고 종료일시와 상태만 남긴다. */
function closeSession_(sheet, row, now, status) {
  sheet
    .getRange(row, HISTORY_COLUMN.CLOSED_AT, 1, 2)
    .setValues([[now, status]]);
  sheet.getRange(row, HISTORY_COLUMN.CLOSED_AT).setNumberFormat('yyyy-mm-dd hh:mm:ss');
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
