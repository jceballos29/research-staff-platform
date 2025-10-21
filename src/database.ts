import { Customer, ExclusivePeriod, Member, Mission, ProposalStatus, Resource, Service, Tracking } from "./type"

export const customers: Customer[] = [
  {
    id: '684C7C6F-4ECB-44CC-ADB7-65F3B9D584CE',
    name: 'ODEC CENTRO DE CALCULO Y APLICACIONES INFORMATICAS, S.A.',
    cif: 'A46063418',
    crmAccountId: '7d86adca-a3bf-e611-80ea-c4346badc0e4'
  },
  // {
  //   id: '68FAA0FB-049C-491B-A24A-7E5DA818EDF4',
  //   name: '100M MONTADITOS INTERNACIONAL, S.L.',
  //   cif: 'B85777654',
  //   crmAccountId: '18f584d9-63c0-e611-80eb-c4346badc0e4'
  // },
  // {
  //   id: '11993772-E9E9-4E8C-80D9-F4766EEDE7C4',
  //   name: 'NTT DATA SPAIN, S.L.U.',
  //   cif: 'B82387770',
  //   crmAccountId: 'ee49e58f-a3bf-e611-80e9-c4346badd004'
  // },
  // {
  //   id: '805F8550-77D5-4A81-B2E5-A64211A22142',
  //   name: 'ATOS SPAIN, S.A.',
  //   cif: 'A28240752',
  //   crmAccountId: 'e0777e3b-abbf-e611-80e9-c4346bad6048'
  // }
]

export const missions: Mission[] = [
  {
    id: 'C787C02C-3021-43E0-930B-543482322BB5',
    areaCode: 'G63',
    title: 'ODECC-BBSS25',
    softDeleted: false,
    crmAccountId: '7d86adca-a3bf-e611-80ea-c4346badc0e4'
  },
  {
    id: '08D6B776-E6DD-4313-A05A-547F7D3262AF',
    areaCode: 'G63',
    title: 'ODECC-BBSS24',
    softDeleted: false,
    crmAccountId: '7d86adca-a3bf-e611-80ea-c4346badc0e4'
  }
]

export const services: Service[] = [
  {
    id: '2A746BD9-1B70-48CC-98E0-29539A5C2298',
    description: 'ODECC-BBSS25',
    customerId: '684C7C6F-4ECB-44CC-ADB7-65F3B9D584CE',
    fiscalYearStart: new Date('2025-04-01T00:00:00'),
    isVisibleToResource: true,
    hasTrimestreEvidence: true,
    hasTrackProgram: false,
    responsibilitiesIsEditable: false,
    allowDeleteEvidence: false,
    missionId: 'C787C02C-3021-43E0-930B-543482322BB5',
  }
]

export const members: Member[] = [
  {
    id: '0680440D-F6BE-4618-BE8C-079FA6B7F014',
    identityId: '0680440D-F6BE-4618-BE8C-079FA6B7F014',
    fullName: 'Miguel Torres',
    email: 'miguel.torres@odec.com',
    ministryName: 'Torres, Miguel'
  },
  {
    id: 'ADC02121-47FA-4EBA-86F8-15C7A36C3898',
    identityId: 'ADC02121-47FA-4EBA-86F8-15C7A36C3898',
    fullName: 'Sofía Ruiz',
    email: 'sofía.ruiz@odec.com',
    ministryName: 'Ruiz, Sofía'
  },
  {
    id: '71A9DFCF-D46C-49C3-B950-1AC15E14B7DE',
    identityId: '71A9DFCF-D46C-49C3-B950-1AC15E14B7DE',
    fullName: 'Lucía Sánchez',
    email: 'lucía.sánchez@odec.com',
    ministryName: 'Sánchez, Lucía'
  },
  {
    id: '2F385EF5-46AC-4B49-9ADC-659C9D1591F4',
    identityId: '2F385EF5-46AC-4B49-9ADC-659C9D1591F4',
    fullName: 'Sofía López',
    email: 'sofía.lópez@odec.com',
    ministryName: 'López, Sofía'
  },
  {
    id: 'DB117BB0-8AA8-4F02-B318-8ACDD083CE15',
    identityId: 'DB117BB0-8AA8-4F02-B318-8ACDD083CE15',
    fullName: 'Marta Ramírez',
    email: 'marta.ramírez@odec.com',
    ministryName: 'Ramírez, Marta'
  },
  {
    id: 'B2F45181-8A61-4652-AA2F-92345DE7FDE9',
    identityId: 'B2F45181-8A61-4652-AA2F-92345DE7FDE9',
    fullName: 'Andrés Sánchez',
    email: 'andrés.sánchez@odec.com',
    ministryName: 'Sánchez, Andrés'
  },
  {
    id: '53CDF598-C7CD-42EA-9582-98CBF66BF8B6',
    identityId: '53CDF598-C7CD-42EA-9582-98CBF66BF8B6',
    fullName: 'Miguel Martínez',
    email: 'miguel.martínez@odec.com',
    ministryName: 'Martínez, Miguel'
  },
  {
    id: '3F22CADB-BDE2-4F24-B239-D1DA6D6B7E0A',
    identityId: '3F22CADB-BDE2-4F24-B239-D1DA6D6B7E0A',
    fullName: 'Elena Torres',
    email: 'elena.torres@odec.com',
    ministryName: 'Torres, Elena'
  },
  {
    id: '5CE05856-EB39-49DF-84DE-E10DB981C4FE',
    identityId: '5CE05856-EB39-49DF-84DE-E10DB981C4FE',
    fullName: 'Lucía Torres',
    email: 'lucía.torres@odec.com',
    ministryName: 'Torres, Lucía'
  },
  {
    id: 'BF5FE32C-E112-4A7B-BF96-EC7173941BF7',
    identityId: 'BF5FE32C-E112-4A7B-BF96-EC7173941BF7',
    fullName: 'Carlos Martínez',
    email: 'carlos.martínez@odec.com',
    ministryName: 'Martínez, Carlos'
  }
]

export const resources: Resource[] = [
  {
    id: '48EEE2B2-83DD-44EB-9861-08BF5E48ED91',
    memberId: '0680440D-F6BE-4618-BE8C-079FA6B7F014',
    serviceId: '2A746BD9-1B70-48CC-98E0-29539A5C2298',
    proposalStatus: ProposalStatus.Approved
  },
  // {
  //   id: '01261C41-E9C1-4ED5-AEC7-300E9FE50F95',
  //   memberId: 'ADC02121-47FA-4EBA-86F8-15C7A36C3898',
  //   serviceId: '2A746BD9-1B70-48CC-98E0-29539A5C2298',
  //   proposalStatus: ProposalStatus.Approved
  // },
  // {
  //   id: '2A07350F-2248-4263-8485-358A8E608290',
  //   memberId: '71A9DFCF-D46C-49C3-B950-1AC15E14B7DE',
  //   serviceId: '2A746BD9-1B70-48CC-98E0-29539A5C2298',
  //   proposalStatus: ProposalStatus.Approved
  // },
  // {
  //   id: '8ED65A62-60D4-43AA-A5CA-4181DAECA203',
  //   memberId: '2F385EF5-46AC-4B49-9ADC-659C9D1591F4',
  //   serviceId: '2A746BD9-1B70-48CC-98E0-29539A5C2298',
  //   proposalStatus: ProposalStatus.Approved
  // },
  // {
  //   id: 'A8F014C8-E488-41B9-BBB4-47BEEE85FE55',
  //   memberId: 'DB117BB0-8AA8-4F02-B318-8ACDD083CE15',
  //   serviceId: '2A746BD9-1B70-48CC-98E0-29539A5C2298',
  //   proposalStatus: ProposalStatus.Approved
  // },
  // {
  //   id: '21C6D03E-C407-4083-BF34-50780959BA33',
  //   memberId: 'B2F45181-8A61-4652-AA2F-92345DE7FDE9',
  //   serviceId: '2A746BD9-1B70-48CC-98E0-29539A5C2298',
  //   proposalStatus: ProposalStatus.Approved
  // },
  // {
  //   id: 'B24BA3DF-5D4B-4327-ACA8-54A4BEBF603B',
  //   memberId: '53CDF598-C7CD-42EA-9582-98CBF66BF8B6',
  //   serviceId: '2A746BD9-1B70-48CC-98E0-29539A5C2298',
  //   proposalStatus: ProposalStatus.Approved
  // },
  // {
  //   id: 'CC0EA2E5-BE53-44FA-AA9D-817B21576C77',
  //   memberId: '3F22CADB-BDE2-4F24-B239-D1DA6D6B7E0A',
  //   serviceId: '2A746BD9-1B70-48CC-98E0-29539A5C2298',
  //   proposalStatus: ProposalStatus.Approved
  // },
  // {
  //   id: 'E78712D0-B1CB-4F86-9404-87CEB1114C2F',
  //   memberId: '5CE05856-EB39-49DF-84DE-E10DB981C4FE',
  //   serviceId: '2A746BD9-1B70-48CC-98E0-29539A5C2298',
  //   proposalStatus: ProposalStatus.Approved
  // },
  // {
  //   id: '124667E3-E238-4553-AA94-FC9DD84FA644',
  //   memberId: 'BF5FE32C-E112-4A7B-BF96-EC7173941BF7',
  //   serviceId: '2A746BD9-1B70-48CC-98E0-29539A5C2298',
  //   proposalStatus: ProposalStatus.Approved
  // }
]

export const periods: ExclusivePeriod[] = [
  {
    id: '6dd28ecd-c0c3-43bd-8402-f37160af6905',
    resourceId: '48EEE2B2-83DD-44EB-9861-08BF5E48ED91',
    number: 1,
    startDate: new Date('2025-04-01T00:00:00'),

  },
  {
    id: 'a56f8206-194a-4062-8f09-72a7263a66f0',
    resourceId: '01261C41-E9C1-4ED5-AEC7-300E9FE50F95',
    number: 1,
    startDate: new Date('2025-05-09T00:00:00'),
  },
  {
    id: '26c691d3-43db-4883-b8e2-557c055075b0',
    resourceId: '2A07350F-2248-4263-8485-358A8E608290',
    number: 1,
    startDate: new Date('2025-05-17T00:00:00'),
  },
  {
    id: 'a3f1d3e2-6efd-4060-bdce-df5a992342dd',
    resourceId: '8ED65A62-60D4-43AA-A5CA-4181DAECA203',
    number: 1,
    startDate: new Date('2025-04-03T00:00:00'),
  },
  {
    id: '6682fbe9-bc97-4eb2-b0c0-270785744d56',
    resourceId: 'A8F014C8-E488-41B9-BBB4-47BEEE85FE55',
    number: 1,
    startDate: new Date('2025-05-02T00:00:00'),
  },
  {
    id: '26b9be15-d16a-43ac-b096-6a7879cb20fe',
    resourceId: '21C6D03E-C407-4083-BF34-50780959BA33',
    number: 1,
    startDate: new Date('2025-04-03T00:00:00'),
  },
  {
    id: '7a0f7b4a-237a-4e0f-be82-8029135498ce',
    resourceId: 'B24BA3DF-5D4B-4327-ACA8-54A4BEBF603B',
    number: 1,
    startDate: new Date('2025-05-18T00:00:00'),
  },
  {
    id: '88154c48-387c-4422-93e7-d5de239c3075',
    resourceId: 'CC0EA2E5-BE53-44FA-AA9D-817B21576C77',
    number: 1,
    startDate: new Date('2025-05-18T00:00:00'),
  },
  {
    id: '057ccaee-89b8-4ac7-a82d-bef448caecd9',
    resourceId: 'E78712D0-B1CB-4F86-9404-87CEB1114C2F',
    number: 1,
    startDate: new Date('2025-05-13T00:00:00'),
  },
  {
    id: 'b4c65463-2644-4b11-b086-d94b33d6f62a',
    resourceId: '124667E3-E238-4553-AA94-FC9DD84FA644',
    number: 1,
    startDate: new Date('2025-04-17T00:00:00'),
  }
]

export const trackings: Tracking[] = []
