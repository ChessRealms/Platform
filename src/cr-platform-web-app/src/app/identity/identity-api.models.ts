export interface RegistrationError {
  code: string;
  description: string;
}

export interface ConfirmationMetadata {
  sessionToken: string | null;
  expiresAt: string;
  deliveryStatus: string;
  resendAvailableAt: string | null;
  failureCode: string | null;
}

export interface RegistrationRequest {
  email: string;
  username: string;
  password: string;
}

export interface RegistrationResult {
  userId: string | null;
  emailConfirmation?: ConfirmationMetadata | null;
  errors: RegistrationError[];
}

export interface ConfirmationSessionResult {
  code: string;
  emailConfirmation: ConfirmationMetadata | null;
}

export interface ConfirmationResult {
  code: string;
}

export interface LoginRequest {
  email: string;
  password: string;
}

export interface WebSessionResult {
  code: string;
  accessToken: string | null;
  tokenType: string | null;
  expiresIn: number | null;
  emailConfirmation?: ConfirmationMetadata | null;
  refreshTokenExpiresAt: string | null;
}

export interface CurrentUser {
  userId: string;
  username: string | null;
}

export interface LoginSession {
  id: string;
  createdAt: string;
  expiresAt: string;
  isCurrent: boolean;
}
export interface ResetPasswordRequest {
  userId: string;
  token: string;
  newPassword: string;
}
