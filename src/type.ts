

export interface Customer {
  id: string;
  name: string;
  cif: string;
  crmAccountId: string;
}

export interface Mission {
  id: string;
  areaCode: string;
  title: string;
  softDeleted: boolean;
  crmAccountId: string;
  expired?: Date;
}

export interface Service {
  id: string;
  description: string;
  customerId: string;
  // customer?: Customer;
  fiscalYearStart: Date;
  isVisibleToResource: boolean;
  hasTrimestreEvidence: boolean;
  hasTrackProgram: boolean;
  responsibilitiesIsEditable: boolean;
  allowDeleteEvidence: boolean;
  missionId: string;
  // mission: Mission;
}

export interface Member {
  id: string;
  identityId: string;
  fullName: string;
  email: string;
  ministryName: string;
}

export interface Resource {
  id: string;
  memberId: string;
  // member: Member;
  serviceId: string;
  // service: Service;

  // employeeNumber: string;
  // docId: string;
  // naf: string;
  // isAuditSelected: boolean;

  // degree: string;
  // department: string;

  // countryId: string;
  // country: Country;

  // actualRoleId: string;
  // actualRole: ActualRole;

  // periodicity: Periodicity;

  // experience: string;
  // isLastYearResource: boolean;
  // taxGroup: number;

  // province: string;
  // responsibilities: string;
  // comments: string;
  // commentsVerification: string;

  // plannedTasks: string;
  // workHours: number;
  // employeeNumber2: string;
  // jobLocationType: JobLocation;
  // companyCost: number;
  // ccc: string;

  // responsiblesResource: ResourceResponsibleReport[];
  // managers: ResourceManager[];

  proposalStatus: ProposalStatus;
  periods?: ExclusivePeriod[];
  // profileStatus: ProfileStatus;

  // hasLeaveDays: boolean;
  // leaveDays: number;

  // modifiedFields: string;

  // createdBy: string;
  // created: Date;
  // modifiedBy: string;
  // modified: Date;

  // periods: ExclusivePeriod[];
  // contracts: ResourceContract[];
  // trackings: Tracking[];

  // companyId: string;
  // company: Company;

  // regulatoryFramework: Regulations;
  // rewardable: BonusStatus;

  // bonusLowDate: Date;
  // isDismissalCompany: boolean;

  // idcPeriods: IdcPeriod[];
  // rntPeriods: RntPeriod[];
  // affiliations: Affiliation[];

}

export interface ExclusivePeriod {
  id: string;
  resourceId: string;
  number: number;
  startDate: Date;
  endDate?: Date;
}

export interface Tracking {
  id: string;
  resourceId: string;
  serviceId: string;

  year: number;
  month: number;
  contentType: ContentType;
  comments1?: string;
  comments2?: string;
  trackingApproveStatus: TrackingStatus;
  createdBy: string;
  created: Date;
  modifiedBy: string;
  modified: Date;

  hasLeave: number;
  daysLeave: number;

  hasTraining: number;
  hoursTraining: number;

  isF38Selected: boolean;
  f38Type?: string;
  f38Phase?: string; // F38Fase
  f38Comments?: string;
  f38Traceability?: string;
  score: number;

  // documents: TrackingDocument[];

  schedule: string;

  // projectId?: string;
  // project?: Project;

  // programId?: string;
  // program?: Program;

  // Soft Delete Fields
  isDeleted: boolean;
  deletedBy?: string;
  deletedDate?: Date;
}

export interface Country {
  id: string;
  nationality: string;
  natISO: string;
}

export interface ActualRole {
  id: string;
  customerId: string;
  customer?: Customer;
  roleDescription: string;
  functionsAndResponsibilities?: string;
}

export enum Periodicity {
  Daily,
  Weekly,
  Biweekly,
  Monthly
}

export enum ContentType {
  Evidence, // Evidencia
  Training, // Formacion
  Absence   // Ausencia
}

export enum TrackingStatus {
  Draft,
  Sended,
  Approved,
  Rejected,
  NotNecessary
}

export enum ProposalStatus {
  Created,
  Pending,
  Approved,
  Rejected,
  Dismissal, // baja devuelto    
  DismissalNotification, // modificado
  ApprovedSDII,
  Approved100_150
}

export interface FiscalInfo {
  start: Date;
  end: Date;
  periods: Period[];
  currentMonth: Month | undefined;
  currentPeriod: Period | undefined;
}

export interface Month {
  month: number;
  year: number;
  label: string // month/year
}

export interface Period {
  number: number;
  months: Month[];
}